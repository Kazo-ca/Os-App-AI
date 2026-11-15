using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kazo.Ai.Chat;
using Kazo.Ai.Http;

namespace Kazo.Ai.Providers;

/// <summary>
/// A provider of the OpenAI chat completions format: <c>POST {base}/chat/completions</c> with
/// <c>"stream": true</c>, read as Server-Sent Events, and <c>GET {base}/models</c>. OpenRouter,
/// OpenAI, Ollama, LM Studio, vLLM, llama.cpp's server and most gateways speak it.
/// </summary>
/// <remarks>
/// The JSON is written with <see cref="Utf8JsonWriter"/> and read with <see cref="JsonDocument"/>,
/// without reflection, so the app keeps working in the trimmed published OS. <c>top_k</c> and
/// <c>repetition_penalty</c> are not part of OpenAI's own API: they are only sent to the services
/// that read them (<see cref="SendsExtendedParameters"/>).
/// </remarks>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    /// <summary>The address of the app sent to OpenRouter (its <c>HTTP-Referer</c> header).</summary>
    public const string AppReferer = "https://os.kazo.ca/";

    /// <summary>The app's name sent to OpenRouter (its <c>X-Title</c> header).</summary>
    public const string AppTitle = "Kazo OS AI";

    private readonly ProviderSettings _settings;
    private readonly IStreamingHttp _http;
    private readonly Func<CancellationToken, ValueTask<string?>> _apiKey;

    /// <summary>Creates the provider.</summary>
    /// <param name="settings">Its settings.</param>
    /// <param name="http">The transport.</param>
    /// <param name="apiKey">Reads the API key (from the keyring) when a request leaves.</param>
    public OpenAiCompatibleProvider(ProviderSettings settings, IStreamingHttp http, Func<CancellationToken, ValueTask<string?>> apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apiKey);
        _settings = settings;
        _http = http;
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    public string Id => _settings.Id;

    /// <inheritdoc />
    public string Name => _settings.Name;

    /// <summary>Gets whether <c>top_k</c> and <c>repetition_penalty</c> are sent (not to OpenAI itself).</summary>
    public bool SendsExtendedParameters =>
        !_settings.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var typed = (_settings.Models ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => new ModelInfo(id, id)).ToList();
        if (!_settings.HasValidBaseUrl())
        {
            return typed;
        }

        StreamingHttpRequest request = new("GET", _settings.Endpoint("models"), await HeadersAsync(json: false, cancellationToken));
        await using StreamingHttpResponse response = await _http.SendAsync(request, cancellationToken);
        string body = await response.ReadAllAsync(cancellationToken);
        if (!response.IsSuccess)
        {
            throw Failure(response.Status, body);
        }

        var listed = ParseModels(body);
        return [.. typed.Where(model => listed.All(other => other.Id != model.Id)), .. listed];
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_settings.HasValidBaseUrl())
        {
            throw new AiServiceException(AiErrorKind.NotFound, $"The address of {_settings.Name} is not valid: {_settings.BaseUrl}");
        }

        StreamingHttpRequest http = new(
            "POST",
            _settings.Endpoint("chat/completions"),
            await HeadersAsync(json: true, cancellationToken),
            BuildBody(request, SendsExtendedParameters));
        await using StreamingHttpResponse response = await _http.SendAsync(http, cancellationToken);
        if (!response.IsSuccess)
        {
            throw Failure(response.Status, await response.ReadAllAsync(cancellationToken));
        }

        if (!response.ContentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // A service that ignores "stream": a single JSON answer.
            string whole = await response.ReadAllAsync(cancellationToken);
            yield return ParseCompletion(whole);
            yield break;
        }

        var parser = new SseParser();
        await foreach (string chunk in response.ReadChunksAsync(cancellationToken))
        {
            foreach (string data in parser.Push(chunk))
            {
                if (data == "[DONE]")
                {
                    yield break;
                }

                if (ParseStreamEvent(data) is { } delta)
                {
                    yield return delta;
                }
            }
        }

        foreach (string data in parser.Complete())
        {
            if (data != "[DONE]" && ParseStreamEvent(data) is { } delta)
            {
                yield return delta;
            }
        }
    }

    /// <summary>Writes the JSON body of a chat completions request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="extended">Whether <c>top_k</c> and <c>repetition_penalty</c> are written.</param>
    /// <returns>The JSON.</returns>
    public static string BuildBody(ChatRequest request, bool extended)
    {
        ArgumentNullException.ThrowIfNull(request);
        SamplingParameters p = request.Parameters.Clamp();
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("model", request.Model);
            json.WriteBoolean("stream", true);
            json.WriteStartArray("messages");
            foreach (ChatMessage message in request.Messages)
            {
                json.WriteStartObject();
                json.WriteString("role", ChatMessage.RoleName(message.Role));
                json.WriteString("content", message.Content);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteNumber("temperature", p.Temperature);
            json.WriteNumber("top_p", p.TopP);
            json.WriteNumber("frequency_penalty", p.FrequencyPenalty);
            json.WriteNumber("presence_penalty", p.PresencePenalty);
            if (extended)
            {
                if (p.TopK > 0)
                {
                    json.WriteNumber("top_k", p.TopK);
                }

                json.WriteNumber("repetition_penalty", p.RepetitionPenalty);
            }

            if (request.MaxTokens is int max and > 0)
            {
                json.WriteNumber("max_tokens", max);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads one event of the stream (<c>choices[0].delta.content</c>).</summary>
    /// <param name="data">The data of the event.</param>
    /// <returns>The piece, or null for an event with nothing to show.</returns>
    /// <exception cref="AiServiceException">The event is an error (OpenRouter sends errors in the stream).</exception>
    public static ChatDelta? ParseStreamEvent(string data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                int code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) ? n : 0;
                throw new AiServiceException(AiServiceException.KindOf(code), ErrorText(error), code);
            }

            string text = string.Empty;
            string? finish = null;
            if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                JsonElement choice = choices[0];
                if (choice.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? string.Empty;
                }

                if (choice.TryGetProperty("finish_reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String)
                {
                    finish = reason.GetString();
                }
            }

            TokenUsage? usage = ParseUsage(root);
            return text.Length == 0 && finish is null && usage is null ? null : new ChatDelta(text, finish, usage);
        }
    }

    /// <summary>Reads a non-streamed answer (<c>choices[0].message.content</c>).</summary>
    /// <param name="body">The JSON.</param>
    /// <returns>The whole answer as one piece.</returns>
    public static ChatDelta ParseCompletion(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                throw new AiServiceException(AiErrorKind.Unknown, ErrorText(error));
            }

            string text = string.Empty;
            string? finish = null;
            if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                JsonElement choice = choices[0];
                if (choice.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? string.Empty;
                }

                if (choice.TryGetProperty("finish_reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String)
                {
                    finish = reason.GetString();
                }
            }

            return new ChatDelta(text, finish ?? "stop", ParseUsage(root));
        }
        catch (JsonException exception)
        {
            throw new AiServiceException(AiErrorKind.Unknown, "The service sent an answer that is not JSON.", innerException: exception);
        }
    }

    /// <summary>Reads the list of <c>GET /models</c> (<c>data[].id</c>, OpenRouter's <c>name</c> and <c>context_length</c>).</summary>
    /// <param name="body">The JSON.</param>
    /// <returns>The models, sorted by name.</returns>
    public static IReadOnlyList<ModelInfo> ParseModels(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var models = new List<ModelInfo>();
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string modelId = id.GetString() ?? string.Empty;
                if (modelId.Length == 0)
                {
                    continue;
                }

                string name = item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? modelId : modelId;
                int? context = item.TryGetProperty("context_length", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int length) ? length : null;
                models.Add(new ModelInfo(modelId, name, context));
            }

            return [.. models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Turns an error answer into an exception with the service's own message.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="body">The body.</param>
    /// <returns>The exception.</returns>
    public static AiServiceException Failure(int status, string body)
    {
        string message = string.Create(CultureInfo.InvariantCulture, $"HTTP {status}");
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                message = string.Create(CultureInfo.InvariantCulture, $"HTTP {status}: {ErrorText(error)}");
            }
        }
        catch (JsonException)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                string trimmed = body.Trim();
                message = string.Create(CultureInfo.InvariantCulture, $"HTTP {status}: {(trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed)}");
            }
        }

        return new AiServiceException(AiServiceException.KindOf(status), message, status);
    }

    private static string ErrorText(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.String => error.GetString() ?? "error",
        JsonValueKind.Object when error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String => m.GetString() ?? "error",
        _ => error.GetRawText(),
    };

    private static TokenUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int prompt = usage.TryGetProperty("prompt_tokens", out JsonElement p) && p.TryGetInt32(out int pt) ? pt : 0;
        int completion = usage.TryGetProperty("completion_tokens", out JsonElement c) && c.TryGetInt32(out int ct) ? ct : 0;
        return new TokenUsage(prompt, completion);
    }

    private async Task<IReadOnlyDictionary<string, string>> HeadersAsync(bool json, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = json ? "text/event-stream, application/json" : "application/json" };
        if (json)
        {
            headers["Content-Type"] = "application/json";
        }

        if (_settings.NeedsKey)
        {
            string? key = await _apiKey(cancellationToken);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new AiServiceException(AiErrorKind.Auth, $"No API key for {_settings.Name}: add it in the settings.");
            }

            headers["Authorization"] = "Bearer " + key.Trim();
        }

        if (_settings.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            headers["HTTP-Referer"] = AppReferer;
            headers["X-Title"] = AppTitle;
        }

        return headers;
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kazo.Ai.Chat;
using Kazo.Ai.Http;

namespace Kazo.Ai.Providers;

/// <summary>What the local model can do in this browser.</summary>
/// <param name="WebGpu">Whether WebGPU works.</param>
/// <param name="Adapter">The GPU's name, when the browser tells it.</param>
/// <param name="ShaderF16">Whether the GPU runs 16-bit shaders (the <c>q4f16</c> models need it).</param>
/// <param name="Error">Why WebGPU does not work.</param>
public sealed record LocalGpuInfo(bool WebGpu, string Adapter, bool ShaderF16, string? Error);

/// <summary>A model the local engine can run.</summary>
/// <param name="Id">Its WebLLM id.</param>
/// <param name="VramMb">The GPU memory it needs.</param>
/// <param name="LowResource">Whether it suits small GPUs and phones.</param>
public sealed record LocalModelInfo(string Id, int VramMb, bool LowResource);

/// <summary>
/// The local model engine (WebLLM, through <c>wwwroot/js/webllm-bridge.js</c>). Behind an interface
/// so that the provider and the window are tested without a GPU.
/// </summary>
public interface ILocalModelEngine
{
    /// <summary>Raised while a model loads: progress 0 to 1 and a line of text.</summary>
    event Action<double, string>? LoadProgress;

    /// <summary>Gets the model loaded now, or null.</summary>
    string? LoadedModel { get; }

    /// <summary>Tells what the browser can do.</summary>
    /// <returns>The answer.</returns>
    Task<LocalGpuInfo> DetectAsync();

    /// <summary>Lists the models the engine knows.</summary>
    /// <returns>The models.</returns>
    Task<IReadOnlyList<LocalModelInfo>> ListModelsAsync();

    /// <summary>Whether a model is already downloaded.</summary>
    /// <param name="modelId">The model.</param>
    /// <returns>Whether it is in the browser's cache.</returns>
    Task<bool> IsCachedAsync(string modelId);

    /// <summary>Loads a model (downloads it the first time).</summary>
    /// <param name="modelId">The model.</param>
    /// <param name="cancellationToken">Cancels the wait (the download goes on).</param>
    /// <returns>A task.</returns>
    Task LoadAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>Writes an answer.</summary>
    /// <param name="requestJson">The OpenAI-style request.</param>
    /// <param name="cancellationToken">Stops the answer.</param>
    /// <returns>The pieces of the answer.</returns>
    IAsyncEnumerable<ChatDelta> ChatAsync(string requestJson, CancellationToken cancellationToken);

    /// <summary>Frees the GPU.</summary>
    /// <returns>A task.</returns>
    Task UnloadAsync();

    /// <summary>Deletes a downloaded model.</summary>
    /// <param name="modelId">The model.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string modelId);
}

/// <summary>
/// The local provider: the model chosen in the settings, run by <see cref="ILocalModelEngine"/>.
/// Its only model is the one loaded; asking for another loads it first.
/// </summary>
public sealed class WebLlmProvider : IChatProvider
{
    private readonly ILocalModelEngine _engine;
    private readonly Func<string> _model;

    /// <summary>Creates the provider.</summary>
    /// <param name="engine">The engine.</param>
    /// <param name="model">The model chosen in the settings.</param>
    public WebLlmProvider(ILocalModelEngine engine, Func<string> model)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(model);
        _engine = engine;
        _model = model;
    }

    /// <inheritdoc />
    public string Id => ProviderSettings.LocalId;

    /// <inheritdoc />
    public string Name => "Local (WebGPU)";

    /// <inheritdoc />
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        string model = _engine.LoadedModel ?? _model();
        return Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo(model, model)]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LocalGpuInfo gpu = await _engine.DetectAsync();
        if (!gpu.WebGpu)
        {
            throw new AiServiceException(AiErrorKind.Unavailable, "The local model needs WebGPU: " + (gpu.Error ?? "not available here."));
        }

        string model = string.IsNullOrWhiteSpace(request.Model) || request.Model == "default" ? _model() : request.Model;
        if (_engine.LoadedModel != model)
        {
            await _engine.LoadAsync(model, cancellationToken);
        }

        await foreach (ChatDelta delta in _engine.ChatAsync(BuildRequest(request), cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>Writes the request WebLLM reads (its OpenAI-style <c>chat.completions.create</c>).</summary>
    /// <param name="request">The request.</param>
    /// <returns>The JSON.</returns>
    public static string BuildRequest(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SamplingParameters p = request.Parameters.Clamp();
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
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
            json.WriteNumber("repetition_penalty", Math.Max(0.01, p.RepetitionPenalty));
            if (request.MaxTokens is int max and > 0)
            {
                json.WriteNumber("max_tokens", max);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

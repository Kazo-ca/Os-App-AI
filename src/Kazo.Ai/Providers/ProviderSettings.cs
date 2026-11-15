using System.Text.Json.Serialization;

namespace Kazo.Ai.Providers;

/// <summary>The kinds of provider the app knows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderKind>))]
public enum ProviderKind
{
    /// <summary>A service of the OpenAI chat completions format (OpenRouter, OpenAI, Ollama...).</summary>
    [JsonStringEnumMemberName("openai-compatible")]
    OpenAiCompatible,

    /// <summary>The local model, in the browser, on the GPU (WebLLM over WebGPU).</summary>
    [JsonStringEnumMemberName("webllm")]
    WebLlm,
}

/// <summary>
/// One provider of the settings file. The API key is never here: it is in the OS keyring, under
/// <c>provider/&lt;id&gt;</c> (<see cref="Settings.IApiKeyStore"/>).
/// </summary>
/// <param name="Id">A short id, unique in the file (<c>openrouter</c>, <c>ollama</c>...).</param>
/// <param name="Name">The name shown.</param>
/// <param name="Kind">The kind.</param>
/// <param name="BaseUrl">The address ending in <c>/v1</c> (for an OpenAI-compatible service).</param>
/// <param name="Enabled">Whether its models are offered.</param>
/// <param name="NeedsKey">Whether the service needs an API key (a local Ollama does not).</param>
/// <param name="Models">Model ids typed by the person, offered even when the service cannot list them.</param>
public sealed record ProviderSettings(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] ProviderKind Kind = ProviderKind.OpenAiCompatible,
    [property: JsonPropertyName("baseUrl")] string BaseUrl = "",
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("needsKey")] bool NeedsKey = true,
    [property: JsonPropertyName("models")] IReadOnlyList<string>? Models = null)
{
    /// <summary>The id of the local model provider, always present.</summary>
    public const string LocalId = "local";

    /// <summary>The address <paramref name="path"/> under <see cref="BaseUrl"/>.</summary>
    /// <param name="path">For example <c>chat/completions</c>.</param>
    /// <returns>The absolute address.</returns>
    public string Endpoint(string path) => BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>Whether <see cref="BaseUrl"/> is an address the browser may call (https, or http on this machine).</summary>
    /// <returns>True for <c>https://…</c>, and for <c>http://localhost</c> or <c>http://127.0.0.1</c>.</returns>
    public bool HasValidBaseUrl()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host == "localhost"));
    }
}

/// <summary>The providers proposed on a first run, which the person completes with a key.</summary>
public static class ProviderPresets
{
    /// <summary>OpenRouter: one key for hundreds of models of many vendors.</summary>
    public static ProviderSettings OpenRouter { get; } =
        new("openrouter", "OpenRouter", ProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1");

    /// <summary>OpenAI.</summary>
    public static ProviderSettings OpenAi { get; } =
        new("openai", "OpenAI", ProviderKind.OpenAiCompatible, "https://api.openai.com/v1", Enabled: false);

    /// <summary>Ollama on this computer (start it with <c>OLLAMA_ORIGINS=*</c>).</summary>
    public static ProviderSettings Ollama { get; } =
        new("ollama", "Ollama", ProviderKind.OpenAiCompatible, "http://localhost:11434/v1", Enabled: false, NeedsKey: false);

    /// <summary>LM Studio on this computer (its server with CORS on).</summary>
    public static ProviderSettings LmStudio { get; } =
        new("lmstudio", "LM Studio", ProviderKind.OpenAiCompatible, "http://localhost:1234/v1", Enabled: false, NeedsKey: false);

    /// <summary>The local model in the browser.</summary>
    public static ProviderSettings Local { get; } =
        new(ProviderSettings.LocalId, "Local (WebGPU)", ProviderKind.WebLlm, NeedsKey: false);

    /// <summary>Every preset, in the order shown.</summary>
    public static IReadOnlyList<ProviderSettings> All { get; } = [Local, OpenRouter, OpenAi, Ollama, LmStudio];
}

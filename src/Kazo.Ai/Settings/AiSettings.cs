using System.Text.Json.Serialization;
using Kazo.Ai.Chat;
using Kazo.Ai.Providers;
using Kazo.Ai.Tuning;

namespace Kazo.Ai.Settings;

/// <summary>How a message is answered.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatMode>))]
public enum ChatMode
{
    /// <summary>By the selected model.</summary>
    [JsonStringEnumMemberName("single")]
    Single,

    /// <summary>By every model of the race at once; the best answer is kept.</summary>
    [JsonStringEnumMemberName("race")]
    Race,
}

/// <summary>
/// The app's settings, in <c>~/.config/ca.kazo.ai/settings.json</c>. API keys are never here
/// (<see cref="IApiKeyStore"/>). A file written by an older version has missing fields: they take
/// their defaults.
/// </summary>
public sealed record AiSettings
{
    /// <summary>The version of the file format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets the format version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the providers.</summary>
    [JsonPropertyName("providers")]
    public IReadOnlyList<ProviderSettings> Providers { get; init; } = ProviderPresets.All;

    /// <summary>Gets the selected model, as <c>provider:model</c>.</summary>
    [JsonPropertyName("selectedModel")]
    public string SelectedModel { get; init; } = "openrouter:openai/gpt-4o-mini";

    /// <summary>Gets the answer mode.</summary>
    [JsonPropertyName("mode")]
    public ChatMode Mode { get; init; } = ChatMode.Single;

    /// <summary>Gets the models of a race, as <c>provider:model</c>.</summary>
    [JsonPropertyName("raceModels")]
    public IReadOnlyList<string> RaceModels { get; init; } =
    [
        "openrouter:openai/gpt-4o-mini",
        "openrouter:anthropic/claude-3.5-haiku",
        "openrouter:google/gemini-2.0-flash-001",
        "openrouter:meta-llama/llama-3.3-70b-instruct",
    ];

    /// <summary>Gets the points a race answer must win by to replace the shown one.</summary>
    [JsonPropertyName("raceMinDelta")]
    public double RaceMinDelta { get; init; } = 8;

    /// <summary>Gets how the sampling parameters are chosen.</summary>
    [JsonPropertyName("strategy")]
    public AutoTuneStrategy Strategy { get; init; } = AutoTuneStrategy.Adaptive;

    /// <summary>Gets the parameters of the manual strategy.</summary>
    [JsonPropertyName("manualParams")]
    public SamplingParameters ManualParams { get; init; } = SamplingParameters.Default;

    /// <summary>Gets whether the 👍/👎 ratings tune the parameters.</summary>
    [JsonPropertyName("learnFromFeedback")]
    public bool LearnFromFeedback { get; init; } = true;

    /// <summary>Gets the longest answer in tokens (0: the provider's default).</summary>
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; }

    /// <summary>Gets the ids of the enabled modules.</summary>
    [JsonPropertyName("enabledModules")]
    public IReadOnlyList<string> EnabledModules { get; init; } = ["whitespace_tidier"];

    /// <summary>Gets each module's settings (module id → key → value).</summary>
    [JsonPropertyName("moduleConfigs")]
    public IReadOnlyDictionary<string, Dictionary<string, string>> ModuleConfigs { get; init; } = new Dictionary<string, Dictionary<string, string>>();

    /// <summary>Gets the persona of new conversations.</summary>
    [JsonPropertyName("personaId")]
    public string PersonaId { get; init; } = BuiltInPersonas.Assistant.Id;

    /// <summary>Gets the personas added by the person.</summary>
    [JsonPropertyName("personas")]
    public IReadOnlyList<Persona> CustomPersonas { get; init; } = [];

    /// <summary>Gets the local model (a WebLLM model id).</summary>
    [JsonPropertyName("localModel")]
    public string LocalModel { get; init; } = "Qwen2.5-1.5B-Instruct-q4f16_1-MLC";

    /// <summary>
    /// Gets whether the local model is loaded when the window opens, once it was downloaded
    /// (the local provider then connects by itself).
    /// </summary>
    [JsonPropertyName("autoLoadLocal")]
    public bool AutoLoadLocal { get; init; } = true;

    /// <summary>Gets whether the app falls back to the local model when no provider has a key.</summary>
    [JsonPropertyName("preferLocalWithoutKey")]
    public bool PreferLocalWithoutKey { get; init; } = true;

    /// <summary>Every persona: built-in, then the person's.</summary>
    [JsonIgnore]
    public IReadOnlyList<Persona> AllPersonas => [.. BuiltInPersonas.All, .. CustomPersonas.Where(p => BuiltInPersonas.All.All(b => b.Id != p.Id))];

    /// <summary>Finds a provider.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Its settings, or null.</returns>
    public ProviderSettings? FindProvider(string id) => Providers.FirstOrDefault(provider => provider.Id == id);

    /// <summary>Finds a persona (the default one when unknown).</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The persona.</returns>
    public Persona FindPersona(string? id) => AllPersonas.FirstOrDefault(persona => persona.Id == id) ?? BuiltInPersonas.Assistant;

    /// <summary>Fixes what a hand-edited or older file may hold: the local provider is always there, ids unique, values in range.</summary>
    /// <returns>The settings, fixed.</returns>
    public AiSettings Normalize()
    {
        var providers = Providers
            .Where(provider => provider is not null && ProviderIdIsValid(provider.Id))
            .GroupBy(provider => provider.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (providers.All(provider => provider.Id != ProviderSettings.LocalId))
        {
            providers.Insert(0, ProviderPresets.Local);
        }

        return this with
        {
            Version = CurrentVersion,
            Providers = providers,
            RaceModels = [.. (RaceModels ?? []).Where(model => ModelRef.TryParse(model, out _)).Distinct(StringComparer.Ordinal).Take(8)],
            RaceMinDelta = double.IsFinite(RaceMinDelta) ? Math.Clamp(RaceMinDelta, 1, 50) : 8,
            ManualParams = (ManualParams ?? SamplingParameters.Default).Clamp(),
            MaxTokens = Math.Clamp(MaxTokens, 0, 200_000),
            EnabledModules = [.. (EnabledModules ?? []).Distinct(StringComparer.Ordinal)],
            ModuleConfigs = ModuleConfigs ?? new Dictionary<string, Dictionary<string, string>>(),
            CustomPersonas = [.. (CustomPersonas ?? []).Where(persona => persona is not null && ProviderIdIsValid(persona.Id))],
            LocalModel = string.IsNullOrWhiteSpace(LocalModel) ? new AiSettings().LocalModel : LocalModel,
        };
    }

    /// <summary>Whether an id of a provider or persona is valid (1 to 40 letters, digits, '-' or '_').</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool ProviderIdIsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

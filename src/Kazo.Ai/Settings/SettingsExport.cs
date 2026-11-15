using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Ai.Modules;

namespace Kazo.Ai.Settings;

/// <summary>
/// The file of Settings ▸ Export: the settings and the JSON modules, never the API keys. Import
/// reads it back (keys must be typed again on the new computer).
/// </summary>
public sealed record SettingsExport
{
    /// <summary>The value of <see cref="Format"/>.</summary>
    public const string FormatName = "kazo-ai-settings";

    /// <summary>Gets the format name, checked on import.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = FormatName;

    /// <summary>Gets when it was exported.</summary>
    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>Gets the settings.</summary>
    [JsonPropertyName("settings")]
    public AiSettings Settings { get; init; } = new();

    /// <summary>Gets the JSON modules.</summary>
    [JsonPropertyName("modules")]
    public IReadOnlyList<DeclarativeModuleDefinition> Modules { get; init; } = [];

    /// <summary>Writes an export.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="modules">The JSON modules.</param>
    /// <param name="now">The time.</param>
    /// <returns>The JSON.</returns>
    public static string Write(AiSettings settings, IEnumerable<DeclarativeModule> modules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modules);
        var export = new SettingsExport { ExportedAt = now, Settings = settings.Normalize(), Modules = [.. modules.Select(m => m.Definition)] };
        return JsonSerializer.Serialize(export, AiJsonContext.Default.SettingsExport);
    }

    /// <summary>Reads an export.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The settings and the modules.</returns>
    /// <exception cref="FormatException">Not an export of this app, or a module is not valid.</exception>
    public static (AiSettings Settings, IReadOnlyList<DeclarativeModule> Modules) Read(string json)
    {
        SettingsExport? export;
        try
        {
            export = JsonSerializer.Deserialize(json, AiJsonContext.Default.SettingsExport);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The file is not valid JSON: " + exception.Message, exception);
        }

        if (export is null || export.Format != FormatName)
        {
            throw new FormatException($"The file is not a Kazo AI settings export (\"format\": \"{FormatName}\").");
        }

        var modules = export.Modules
            .Select(definition => DeclarativeModule.Parse(JsonSerializer.Serialize(definition, AiJsonContext.Default.DeclarativeModuleDefinition)))
            .ToList();
        return (export.Settings.Normalize(), modules);
    }
}

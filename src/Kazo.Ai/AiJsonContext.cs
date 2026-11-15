using System.Text.Json.Serialization;
using Kazo.Ai.Chat;
using Kazo.Ai.Modules;
using Kazo.Ai.Race;
using Kazo.Ai.Settings;
using Kazo.Ai.Tuning;

namespace Kazo.Ai;

/// <summary>
/// The app's JSON, source-generated: no reflection, so it works in the trimmed published OS (an
/// installed app finds only what the OS kept, Kazo OS ADR 0242).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AiSettings))]
[JsonSerializable(typeof(Conversation))]
[JsonSerializable(typeof(FeedbackState))]
[JsonSerializable(typeof(DeclarativeModuleDefinition))]
[JsonSerializable(typeof(RaceResult))]
[JsonSerializable(typeof(SettingsExport))]
internal sealed partial class AiJsonContext : JsonSerializerContext;

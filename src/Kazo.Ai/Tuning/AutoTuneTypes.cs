using System.Text.Json.Serialization;
using Kazo.Ai.Chat;

namespace Kazo.Ai.Tuning;

/// <summary>
/// What a conversation is about, as guessed from its text, which picks the sampling parameters.
/// The JSON names are lower case (<c>code</c>, <c>creative</c>...), the usual names of these
/// context kinds in parameter-tuning tools.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextType>))]
public enum ContextType
{
    /// <summary>Talk, small questions.</summary>
    [JsonStringEnumMemberName("conversational")]
    Conversational,

    /// <summary>Programming.</summary>
    [JsonStringEnumMemberName("code")]
    Code,

    /// <summary>Stories, poems, ideas.</summary>
    [JsonStringEnumMemberName("creative")]
    Creative,

    /// <summary>Explanations, comparisons, reasoning.</summary>
    [JsonStringEnumMemberName("analytical")]
    Analytical,

    /// <summary>Wild, playful, experimental text.</summary>
    [JsonStringEnumMemberName("chaotic")]
    Chaotic,
}

/// <summary>How the sampling parameters are chosen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AutoTuneStrategy>))]
public enum AutoTuneStrategy
{
    /// <summary>From the conversation (the default).</summary>
    [JsonStringEnumMemberName("adaptive")]
    Adaptive,

    /// <summary>Exact, predictable answers.</summary>
    [JsonStringEnumMemberName("precise")]
    Precise,

    /// <summary>In between.</summary>
    [JsonStringEnumMemberName("balanced")]
    Balanced,

    /// <summary>Varied, imaginative answers.</summary>
    [JsonStringEnumMemberName("creative")]
    Creative,

    /// <summary>Very random answers.</summary>
    [JsonStringEnumMemberName("chaotic")]
    Chaotic,

    /// <summary>The parameters set by hand in the settings, untouched.</summary>
    [JsonStringEnumMemberName("manual")]
    Manual,
}

/// <summary>The score of one context kind for a message.</summary>
/// <param name="Type">The kind.</param>
/// <param name="Score">Its raw score.</param>
/// <param name="Percentage">Its share of all scores, 0 to 100.</param>
public sealed record ContextScore(
    [property: JsonPropertyName("type")] ContextType Type,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("percentage")] double Percentage);

/// <summary>A change of one parameter by the tuner, with its reason.</summary>
/// <param name="Param">The parameter's JSON name.</param>
/// <param name="Before">Its value before.</param>
/// <param name="After">Its value after.</param>
/// <param name="Delta">After minus before.</param>
/// <param name="Reason">Why.</param>
public sealed record ParamDelta(
    [property: JsonPropertyName("param")] string Param,
    [property: JsonPropertyName("before")] double Before,
    [property: JsonPropertyName("after")] double After,
    [property: JsonPropertyName("delta")] double Delta,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// What the tuner decided, with every step shown to the person (the "why these parameters" panel).
/// </summary>
/// <param name="Params">The parameters to send.</param>
/// <param name="DetectedContext">The context kind found.</param>
/// <param name="Confidence">How sure, 0 to 1.</param>
/// <param name="Reasoning">One sentence for the person.</param>
/// <param name="ContextScores">Every kind's score.</param>
/// <param name="ParamDeltas">Every change from the base parameters.</param>
/// <param name="BaselineParams">The base parameters of the strategy or the context.</param>
public sealed record AutoTuneResult(
    [property: JsonPropertyName("params")] SamplingParameters Params,
    [property: JsonPropertyName("detectedContext")] ContextType DetectedContext,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reasoning")] string Reasoning,
    [property: JsonPropertyName("contextScores")] IReadOnlyList<ContextScore> ContextScores,
    [property: JsonPropertyName("paramDeltas")] IReadOnlyList<ParamDelta> ParamDeltas,
    [property: JsonPropertyName("baselineParams")] SamplingParameters BaselineParams);

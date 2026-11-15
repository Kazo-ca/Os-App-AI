using System.Globalization;
using System.Text.Json.Serialization;

namespace Kazo.Ai.Chat;

/// <summary>
/// The sampling parameters of a request. The JSON names are those of the OpenAI and OpenRouter
/// request bodies (<c>temperature</c>, <c>top_p</c>, <c>top_k</c>, <c>frequency_penalty</c>,
/// <c>presence_penalty</c>, <c>repetition_penalty</c>), so a parameter set written by another chat
/// tool with the same six fields reads as is (doc/en/architecture.md, "Data formats").
/// </summary>
/// <param name="Temperature">Randomness, 0 to 2.</param>
/// <param name="TopP">Nucleus sampling, 0 to 1.</param>
/// <param name="TopK">Only the K most likely tokens, 1 to 200 (0: not sent).</param>
/// <param name="FrequencyPenalty">Penalty of repeated tokens by count, -2 to 2.</param>
/// <param name="PresencePenalty">Penalty of tokens already present, -2 to 2.</param>
/// <param name="RepetitionPenalty">Multiplicative repetition penalty, 0 to 2 (1: none).</param>
public sealed record SamplingParameters(
    [property: JsonPropertyName("temperature")] double Temperature = 0.7,
    [property: JsonPropertyName("top_p")] double TopP = 0.9,
    [property: JsonPropertyName("top_k")] int TopK = 50,
    [property: JsonPropertyName("frequency_penalty")] double FrequencyPenalty = 0.1,
    [property: JsonPropertyName("presence_penalty")] double PresencePenalty = 0.1,
    [property: JsonPropertyName("repetition_penalty")] double RepetitionPenalty = 1.0)
{
    /// <summary>The parameters most models use when none are sent.</summary>
    public static SamplingParameters Default { get; } = new();

    /// <summary>Puts every parameter back in its valid range (a hand-edited file, a learned profile).</summary>
    /// <returns>The parameters, clamped.</returns>
    public SamplingParameters Clamp() => new(
        Math.Round(Math.Clamp(Finite(Temperature, 0.7), 0, 2), 2),
        Math.Round(Math.Clamp(Finite(TopP, 0.9), 0, 1), 2),
        Math.Clamp(TopK, 0, 200),
        Math.Round(Math.Clamp(Finite(FrequencyPenalty, 0), -2, 2), 2),
        Math.Round(Math.Clamp(Finite(PresencePenalty, 0), -2, 2), 2),
        Math.Round(Math.Clamp(Finite(RepetitionPenalty, 1), 0, 2), 2));

    /// <summary>Gets one parameter by its JSON name.</summary>
    /// <param name="name">For example <c>top_p</c>.</param>
    /// <returns>Its value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Unknown name.</exception>
    public double Get(string name) => name switch
    {
        "temperature" => Temperature,
        "top_p" => TopP,
        "top_k" => TopK,
        "frequency_penalty" => FrequencyPenalty,
        "presence_penalty" => PresencePenalty,
        "repetition_penalty" => RepetitionPenalty,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown sampling parameter."),
    };

    /// <summary>Returns a copy with one parameter changed.</summary>
    /// <param name="name">Its JSON name.</param>
    /// <param name="value">Its new value.</param>
    /// <returns>The new parameters.</returns>
    public SamplingParameters With(string name, double value) => name switch
    {
        "temperature" => this with { Temperature = value },
        "top_p" => this with { TopP = value },
        "top_k" => this with { TopK = (int)Math.Round(value) },
        "frequency_penalty" => this with { FrequencyPenalty = value },
        "presence_penalty" => this with { PresencePenalty = value },
        "repetition_penalty" => this with { RepetitionPenalty = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown sampling parameter."),
    };

    /// <summary>The six JSON names, in display order.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["temperature", "top_p", "top_k", "frequency_penalty", "presence_penalty", "repetition_penalty"];

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"temperature {Temperature}, top_p {TopP}, top_k {TopK}, frequency {FrequencyPenalty}, presence {PresencePenalty}, repetition {RepetitionPenalty}");

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}

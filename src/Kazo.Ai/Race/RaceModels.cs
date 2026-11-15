using System.Text.Json.Serialization;
using Kazo.Ai.Http;
using Kazo.Ai.Providers;

namespace Kazo.Ai.Race;

/// <summary>
/// One model of a race, once it answered or failed. The JSON names follow the race events of
/// common multi-model tools (<c>model</c>, <c>score</c>, <c>duration_ms</c>, <c>success</c>,
/// <c>error</c>, <c>content_length</c>, <c>models_responded</c>, <c>models_total</c>), so a viewer
/// written for them reads this app's race file (doc/en/architecture.md, "Data formats").
/// </summary>
/// <param name="Model">The model, as <c>provider:model</c>.</param>
/// <param name="Score">Its score, 0 to 100 (0 on failure).</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Success">Whether it answered.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="ContentLength">The length of its answer.</param>
/// <param name="ModelsResponded">Models done so far, this one included.</param>
/// <param name="ModelsTotal">Models of the race.</param>
public sealed record RaceModelResult(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("content_length")] int ContentLength,
    [property: JsonPropertyName("models_responded")] int ModelsResponded,
    [property: JsonPropertyName("models_total")] int ModelsTotal)
{
    /// <summary>Gets the kind of failure, when it failed.</summary>
    [JsonIgnore]
    public AiErrorKind? ErrorKind { get; init; }
}

/// <summary>The best answer so far (the "leader").</summary>
/// <param name="Model">The model.</param>
/// <param name="Score">Its score.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Content">Its answer.</param>
public sealed record RaceLeader(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("content")] string Content);

/// <summary>One line of the final ranking.</summary>
/// <param name="Model">The model.</param>
/// <param name="Score">Its score.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Success">Whether it answered.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="ContentLength">The length of its answer.</param>
/// <param name="Content">Its answer.</param>
public sealed record RaceRanking(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("content_length")] int ContentLength,
    [property: JsonPropertyName("content")] string? Content);

/// <summary>The winner of a race.</summary>
/// <param name="Model">The model.</param>
/// <param name="Score">Its score.</param>
/// <param name="DurationMs">How long it took.</param>
public sealed record RaceWinner(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("duration_ms")] long DurationMs);

/// <summary>Facts about the race as a whole.</summary>
/// <param name="ModelsQueried">Models asked.</param>
/// <param name="ModelsSucceeded">Models that answered.</param>
/// <param name="TotalDurationMs">How long the race took.</param>
/// <param name="Rankings">Every model, best first.</param>
public sealed record RaceSummary(
    [property: JsonPropertyName("models_queried")] int ModelsQueried,
    [property: JsonPropertyName("models_succeeded")] int ModelsSucceeded,
    [property: JsonPropertyName("total_duration_ms")] long TotalDurationMs,
    [property: JsonPropertyName("rankings")] IReadOnlyList<RaceRanking> Rankings);

/// <summary>The outcome of a race.</summary>
/// <param name="Response">The winning answer (empty when every model failed).</param>
/// <param name="Winner">The winner, or null.</param>
/// <param name="Race">The ranking and its facts.</param>
public sealed record RaceResult(
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("winner")] RaceWinner? Winner,
    [property: JsonPropertyName("race")] RaceSummary Race);

/// <summary>What a race listens to while it runs (all optional).</summary>
public sealed class RaceCallbacks
{
    /// <summary>Gets or sets what happens when the race starts (the number of models).</summary>
    public Action<int>? OnRaceStart { get; init; }

    /// <summary>Gets or sets what happens as a model writes (model, text so far).</summary>
    public Action<ModelRef, string>? OnModelText { get; init; }

    /// <summary>Gets or sets what happens when a model is done.</summary>
    public Action<RaceModelResult>? OnModelResult { get; init; }

    /// <summary>Gets or sets what happens when a better answer arrives.</summary>
    public Action<RaceLeader>? OnLeaderChange { get; init; }
}

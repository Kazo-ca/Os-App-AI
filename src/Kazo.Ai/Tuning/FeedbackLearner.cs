using System.Text.Json.Serialization;
using Kazo.Ai.Chat;

namespace Kazo.Ai.Tuning;

/// <summary>Plain measures of an answer, kept with each rating.</summary>
/// <param name="ResponseLength">Characters.</param>
/// <param name="RepetitionScore">0 (no repeated three-word groups) to 1.</param>
/// <param name="AverageSentenceLength">Words per sentence.</param>
/// <param name="VocabularyDiversity">Distinct words over words.</param>
public sealed record ResponseHeuristics(
    [property: JsonPropertyName("responseLength")] int ResponseLength,
    [property: JsonPropertyName("repetitionScore")] double RepetitionScore,
    [property: JsonPropertyName("averageSentenceLength")] double AverageSentenceLength,
    [property: JsonPropertyName("vocabularyDiversity")] double VocabularyDiversity);

/// <summary>One rating (👍 = 1, 👎 = -1) of an answer, with the parameters that wrote it.</summary>
/// <param name="MessageId">The answer's id.</param>
/// <param name="Timestamp">When, in Unix milliseconds.</param>
/// <param name="ContextType">The context of the request.</param>
/// <param name="Model">The model, as <c>provider:model</c>.</param>
/// <param name="Persona">The persona's id.</param>
/// <param name="Params">The parameters sent.</param>
/// <param name="Rating">1 or -1.</param>
/// <param name="Heuristics">The answer's measures.</param>
public sealed record FeedbackRecord(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("contextType")] ContextType ContextType,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("persona")] string Persona,
    [property: JsonPropertyName("params")] SamplingParameters Params,
    [property: JsonPropertyName("rating")] int Rating,
    [property: JsonPropertyName("heuristics")] ResponseHeuristics Heuristics);

/// <summary>What the ratings of one context taught.</summary>
/// <param name="ContextType">The context.</param>
/// <param name="SampleCount">Ratings.</param>
/// <param name="PositiveCount">👍.</param>
/// <param name="NegativeCount">👎.</param>
/// <param name="PositiveParams">Moving average of the parameters of liked answers.</param>
/// <param name="NegativeParams">Moving average of the parameters of disliked answers.</param>
/// <param name="LastUpdated">Unix milliseconds.</param>
public sealed record LearnedProfile(
    [property: JsonPropertyName("contextType")] ContextType ContextType,
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    [property: JsonPropertyName("positiveCount")] int PositiveCount,
    [property: JsonPropertyName("negativeCount")] int NegativeCount,
    [property: JsonPropertyName("positiveParams")] SamplingParameters PositiveParams,
    [property: JsonPropertyName("negativeParams")] SamplingParameters NegativeParams,
    [property: JsonPropertyName("lastUpdated")] long LastUpdated);

/// <summary>Every rating and what they taught; saved in <c>feedback.json</c>.</summary>
/// <param name="History">The latest ratings (at most <see cref="FeedbackLearner.MaxHistory"/>).</param>
/// <param name="LearnedProfiles">One profile per context that has ratings.</param>
public sealed record FeedbackState(
    [property: JsonPropertyName("history")] IReadOnlyList<FeedbackRecord> History,
    [property: JsonPropertyName("learnedProfiles")] IReadOnlyDictionary<ContextType, LearnedProfile> LearnedProfiles)
{
    /// <summary>No rating yet.</summary>
    public static FeedbackState Empty { get; } = new([], new Dictionary<ContextType, LearnedProfile>());
}

/// <summary>
/// Learns from the person's 👍 and 👎: per context, an exponential moving average of the parameters
/// of liked answers and of disliked ones. The adjustment pushes away from the disliked average and
/// toward the liked one, from <see cref="MinSamples"/> ratings on, with a weight that grows with the
/// number of ratings up to <see cref="MaxWeight"/>.
/// </summary>
public static class FeedbackLearner
{
    /// <summary>The weight of a new rating in the moving average.</summary>
    public const double Alpha = 0.3;

    /// <summary>The ratings kept.</summary>
    public const int MaxHistory = 500;

    /// <summary>The ratings needed before anything is applied.</summary>
    public const int MinSamples = 3;

    /// <summary>The largest share of the learned direction applied.</summary>
    public const double MaxWeight = 0.5;

    /// <summary>The ratings that reach <see cref="MaxWeight"/>.</summary>
    public const int SamplesForMaxWeight = 20;

    /// <summary>Records a rating.</summary>
    /// <param name="state">The current state.</param>
    /// <param name="record">The rating.</param>
    /// <returns>The new state.</returns>
    public static FeedbackState Record(FeedbackState state, FeedbackRecord record)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);
        int rating = record.Rating >= 0 ? 1 : -1;
        var history = state.History.Where(existing => existing.MessageId != record.MessageId).Append(record with { Rating = rating }).TakeLast(MaxHistory).ToList();

        // Rebuilt from the history, so changing a rating (👍 then 👎) counts once.
        var profiles = new Dictionary<ContextType, LearnedProfile>();
        foreach (FeedbackRecord item in history)
        {
            SamplingParameters p = item.Params.Clamp();
            if (!profiles.TryGetValue(item.ContextType, out LearnedProfile? profile))
            {
                profile = new LearnedProfile(item.ContextType, 0, 0, 0, SamplingParameters.Default, SamplingParameters.Default, item.Timestamp);
            }

            bool positive = item.Rating > 0;
            profiles[item.ContextType] = profile with
            {
                SampleCount = profile.SampleCount + 1,
                PositiveCount = profile.PositiveCount + (positive ? 1 : 0),
                NegativeCount = profile.NegativeCount + (positive ? 0 : 1),
                PositiveParams = positive ? Ema(profile.PositiveParams, p, profile.PositiveCount == 0) : profile.PositiveParams,
                NegativeParams = positive ? profile.NegativeParams : Ema(profile.NegativeParams, p, profile.NegativeCount == 0),
                LastUpdated = Math.Max(profile.LastUpdated, item.Timestamp),
            };
        }

        return new FeedbackState(history, profiles);
    }

    /// <summary>Applies what was learned for a context.</summary>
    /// <param name="current">The parameters chosen so far.</param>
    /// <param name="context">The context.</param>
    /// <param name="state">The state.</param>
    /// <returns>The adjusted parameters (unchanged below <see cref="MinSamples"/> ratings).</returns>
    public static SamplingParameters Apply(SamplingParameters current, ContextType context, FeedbackState state)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(state);
        if (!state.LearnedProfiles.TryGetValue(context, out LearnedProfile? profile) || profile.SampleCount < MinSamples)
        {
            return current;
        }

        double weight = MaxWeight * Math.Min(1, profile.SampleCount / (double)SamplesForMaxWeight);
        SamplingParameters result = current;
        foreach (string name in SamplingParameters.Names)
        {
            double value = current.Get(name);
            if (profile.PositiveCount > 0)
            {
                value += (profile.PositiveParams.Get(name) - value) * weight;
            }

            if (profile.NegativeCount > 0)
            {
                // Away from what was disliked, by half the weight.
                value -= (profile.NegativeParams.Get(name) - value) * weight * 0.5;
            }

            result = result.With(name, value);
        }

        return result.Clamp();
    }

    /// <summary>Measures an answer.</summary>
    /// <param name="response">The answer.</param>
    /// <returns>Its measures.</returns>
    public static ResponseHeuristics Measure(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string[] words = response.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int sentences = Math.Max(1, response.Count(c => c is '.' or '!' or '?'));
        double diversity = words.Length == 0 ? 0 : words.Distinct(StringComparer.OrdinalIgnoreCase).Count() / (double)words.Length;
        var trigrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 2 < words.Length; i++)
        {
            string key = words[i] + " " + words[i + 1] + " " + words[i + 2];
            trigrams[key] = trigrams.GetValueOrDefault(key) + 1;
        }

        double repetition = trigrams.Count == 0 ? 0 : trigrams.Values.Count(count => count > 1) / (double)trigrams.Count;
        return new ResponseHeuristics(
            response.Length,
            Math.Round(repetition, 3),
            Math.Round(words.Length / (double)sentences, 1),
            Math.Round(diversity, 3));
    }

    private static SamplingParameters Ema(SamplingParameters average, SamplingParameters sample, bool first)
    {
        if (first)
        {
            return sample;
        }

        SamplingParameters result = average;
        foreach (string name in SamplingParameters.Names)
        {
            result = result.With(name, (Alpha * sample.Get(name)) + ((1 - Alpha) * average.Get(name)));
        }

        return result.Clamp();
    }
}

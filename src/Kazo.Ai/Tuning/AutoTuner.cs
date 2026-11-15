using System.Globalization;
using Kazo.Ai.Chat;

namespace Kazo.Ai.Tuning;

/// <summary>
/// Chooses the sampling parameters of a request before it leaves, in one step: it guesses the
/// context (<see cref="ContextDetector"/>), starts from that context's profile (or the strategy's),
/// adjusts for the conversation (a long one repeats less, a "be precise" lowers the temperature),
/// then applies what the person's ratings taught (<see cref="FeedbackLearner"/>). Every change is
/// listed with its reason (<see cref="AutoTuneResult.ParamDeltas"/>).
/// </summary>
public static class AutoTuner
{
    /// <summary>The parameters of each fixed strategy, chosen for this app.</summary>
    public static IReadOnlyDictionary<AutoTuneStrategy, SamplingParameters> StrategyProfiles { get; } =
        new Dictionary<AutoTuneStrategy, SamplingParameters>
        {
            [AutoTuneStrategy.Precise] = new(0.25, 0.85, 40, 0.2, 0.0, 1.05),
            [AutoTuneStrategy.Balanced] = new(0.7, 0.9, 50, 0.1, 0.1, 1.0),
            [AutoTuneStrategy.Creative] = new(1.05, 0.95, 90, 0.35, 0.5, 1.1),
            [AutoTuneStrategy.Chaotic] = new(1.5, 0.98, 120, 0.6, 0.7, 1.2),
        };

    /// <summary>The base parameters of each context kind (adaptive strategy).</summary>
    public static IReadOnlyDictionary<ContextType, SamplingParameters> ContextProfiles { get; } =
        new Dictionary<ContextType, SamplingParameters>
        {
            [ContextType.Code] = new(0.2, 0.85, 35, 0.15, 0.0, 1.05),
            [ContextType.Analytical] = new(0.45, 0.88, 45, 0.2, 0.1, 1.05),
            [ContextType.Conversational] = new(0.75, 0.9, 50, 0.15, 0.2, 1.0),
            [ContextType.Creative] = new(1.0, 0.95, 85, 0.35, 0.5, 1.1),
            [ContextType.Chaotic] = new(1.4, 0.97, 110, 0.55, 0.65, 1.15),
        };

    /// <summary>Chooses the parameters of a request.</summary>
    /// <param name="messages">The conversation, the new message last.</param>
    /// <param name="strategy">The strategy.</param>
    /// <param name="manual">The hand-set parameters (the <see cref="AutoTuneStrategy.Manual"/> strategy).</param>
    /// <param name="learned">What the ratings taught, or null.</param>
    /// <returns>The decision.</returns>
    public static AutoTuneResult Tune(
        IReadOnlyList<ChatMessage> messages,
        AutoTuneStrategy strategy,
        SamplingParameters? manual = null,
        FeedbackState? learned = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        IReadOnlyList<ContextScore> scores = ContextDetector.Score(messages);
        ContextScore top = scores[0];
        double confidence = Math.Round(top.Percentage / 100, 2);

        if (strategy == AutoTuneStrategy.Manual)
        {
            SamplingParameters fixedParams = (manual ?? SamplingParameters.Default).Clamp();
            return new AutoTuneResult(fixedParams, top.Type, confidence, "Parameters set by hand.", scores, [], fixedParams);
        }

        SamplingParameters baseline = strategy == AutoTuneStrategy.Adaptive
            ? ContextProfiles[top.Type]
            : StrategyProfiles[strategy];
        var deltas = new List<ParamDelta>();
        SamplingParameters current = baseline;

        // A low confidence moves the adaptive profile part of the way back to balanced.
        if (strategy == AutoTuneStrategy.Adaptive && confidence < 0.5)
        {
            SamplingParameters balanced = StrategyProfiles[AutoTuneStrategy.Balanced];
            double blend = 0.5 - confidence;
            foreach (string name in SamplingParameters.Names)
            {
                double value = current.Get(name) + ((balanced.Get(name) - current.Get(name)) * blend);
                current = Change(current, name, value, "unsure context: closer to balanced", deltas);
            }
        }

        // A long conversation: repeat less.
        int turns = messages.Count(message => message.Role != ChatRole.System);
        if (turns > 10)
        {
            current = Change(current, "frequency_penalty", current.FrequencyPenalty + 0.1, "long conversation: fewer repetitions", deltas);
            current = Change(current, "repetition_penalty", current.RepetitionPenalty + 0.05, "long conversation: fewer repetitions", deltas);
        }

        // Explicit wishes of the last message.
        string last = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Content.ToLowerInvariant() ?? string.Empty;
        if (ContainsAny(last, "precise", "précis", "exact", "exactly", "step by step", "étape par étape", "accurate"))
        {
            current = Change(current, "temperature", current.Temperature - 0.2, "the message asks for precision", deltas);
        }

        if (ContainsAny(last, "creative", "créatif", "original", "surprise", "surprends", "unusual", "inattendu"))
        {
            current = Change(current, "temperature", current.Temperature + 0.2, "the message asks for originality", deltas);
            current = Change(current, "presence_penalty", current.PresencePenalty + 0.1, "the message asks for originality", deltas);
        }

        // Learned from the ratings.
        if (learned is not null)
        {
            SamplingParameters adjusted = FeedbackLearner.Apply(current, top.Type, learned);
            foreach (string name in SamplingParameters.Names)
            {
                current = Change(current, name, adjusted.Get(name), "learned from your ratings", deltas);
            }
        }

        SamplingParameters final = current.Clamp();
        string reasoning = string.Create(
            CultureInfo.InvariantCulture,
            $"{Describe(top.Type)} ({top.Percentage:0}%), {(strategy == AutoTuneStrategy.Adaptive ? "adaptive" : strategy.ToString().ToLowerInvariant())} profile");
        return new AutoTuneResult(final, top.Type, confidence, reasoning, scores, deltas, baseline);
    }

    private static SamplingParameters Change(SamplingParameters current, string name, double value, string reason, List<ParamDelta> deltas)
    {
        SamplingParameters next = current.With(name, value).Clamp();
        double before = current.Get(name);
        double after = next.Get(name);
        if (Math.Abs(after - before) >= 0.005)
        {
            deltas.Add(new ParamDelta(name, before, after, Math.Round(after - before, 2), reason));
        }

        return next;
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.Ordinal));

    private static string Describe(ContextType type) => type switch
    {
        ContextType.Code => "Code",
        ContextType.Creative => "Creative writing",
        ContextType.Analytical => "Analysis",
        ContextType.Chaotic => "Playful",
        _ => "Conversation",
    };
}

using System.Text.RegularExpressions;
using Kazo.Ai.Chat;

namespace Kazo.Ai.Tuning;

/// <summary>
/// Guesses what a conversation is about from its words, in English and French. The last message
/// counts fully and the earlier ones of the person count a third, so a long code conversation keeps
/// its context when the person writes "thanks, and now?".
/// </summary>
public static partial class ContextDetector
{
    /// <summary>How many earlier messages of the person are read.</summary>
    public const int HistoryMessages = 4;

    private static readonly IReadOnlyDictionary<ContextType, string[]> Keywords = new Dictionary<ContextType, string[]>
    {
        [ContextType.Code] =
        [
            "code", "function", "fonction", "class", "classe", "method", "méthode", "bug", "error", "erreur", "exception",
            "compile", "compiler", "debug", "déboguer", "api", "sql", "regex", "json", "html", "css", "javascript", "typescript",
            "python", "c#", "csharp", "rust", "java", "blazor", "razor", "variable", "loop", "boucle", "array", "tableau",
            "refactor", "test", "unit", "git", "commit", "npm", "dotnet", "script", "algorithm", "algorithme", "stack trace",
        ],
        [ContextType.Creative] =
        [
            "story", "histoire", "poem", "poème", "poésie", "write a", "écris", "imagine", "fiction", "character", "personnage",
            "plot", "intrigue", "song", "chanson", "lyrics", "paroles", "novel", "roman", "fantasy", "dialogue", "scene", "scène",
            "brainstorm", "ideas", "idées", "slogan", "metaphor", "métaphore", "haiku", "tale", "conte",
        ],
        [ContextType.Analytical] =
        [
            "explain", "explique", "why", "pourquoi", "how does", "comment fonctionne", "compare", "comparer", "difference",
            "différence", "analyze", "analyse", "pros and cons", "avantages", "inconvénients", "evaluate", "évaluer", "summarize",
            "résume", "résumé", "data", "données", "statistics", "statistiques", "calculate", "calcule", "prove", "démontre",
            "cause", "impact", "research", "recherche", "strategy", "stratégie",
        ],
        [ContextType.Chaotic] =
        [
            "chaos", "chaotic", "chaotique", "random", "aléatoire", "absurd", "absurde", "weird", "bizarre", "surreal",
            "surréaliste", "nonsense", "n'importe quoi", "wild", "crazy", "fou", "glitch", "unhinged",
        ],
        [ContextType.Conversational] =
        [
            "hello", "hi", "hey", "bonjour", "salut", "thanks", "merci", "how are you", "ça va", "what do you think",
            "qu'en penses-tu", "tell me", "dis-moi", "chat", "jaser", "ok", "cool",
        ],
    };

    /// <summary>Scores every context kind for a conversation.</summary>
    /// <param name="messages">The conversation.</param>
    /// <returns>Every kind's score, best first.</returns>
    public static IReadOnlyList<ContextScore> Score(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var userMessages = messages.Where(message => message.Role == ChatRole.User).Select(message => message.Content).ToList();
        var scores = Enum.GetValues<ContextType>().ToDictionary(type => type, _ => 0.0);
        if (userMessages.Count == 0)
        {
            scores[ContextType.Conversational] = 1;
        }
        else
        {
            AddScores(scores, userMessages[^1], 1.0);
            foreach (string earlier in userMessages.Take(userMessages.Count - 1).TakeLast(HistoryMessages))
            {
                AddScores(scores, earlier, 1.0 / 3);
            }
        }

        // A short message with nothing found is small talk.
        if (scores.Values.All(score => score == 0))
        {
            scores[ContextType.Conversational] = 0.5;
        }

        // A loop, not Enumerable.Sum(IEnumerable<double>): the trimmed framework of Kazo OS does not keep it.
        double total = 0;
        foreach (double score in scores.Values)
        {
            total += score;
        }

        return [.. scores
            .Select(pair => new ContextScore(pair.Key, Math.Round(pair.Value, 2), total <= 0 ? 0 : Math.Round(pair.Value / total * 100, 1)))
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.Type)];
    }

    private static void AddScores(Dictionary<ContextType, double> scores, string text, double weight)
    {
        string lower = text.ToLowerInvariant();
        foreach ((ContextType type, string[] words) in Keywords)
        {
            foreach (string word in words)
            {
                if (ContainsWord(lower, word))
                {
                    scores[type] += weight;
                }
            }
        }

        // Code-looking text: a fenced block, braces and semicolons, a call.
        if (lower.Contains("```", StringComparison.Ordinal))
        {
            scores[ContextType.Code] += 3 * weight;
        }

        if (CodeShape().IsMatch(text))
        {
            scores[ContextType.Code] += 1.5 * weight;
        }

        // Many exclamation marks and capitals: playful.
        if (text.Count(c => c == '!') >= 4)
        {
            scores[ContextType.Chaotic] += 0.5 * weight;
        }
    }

    private static bool ContainsWord(string lower, string word)
    {
        int index = 0;
        while ((index = lower.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = index == 0 || !char.IsLetterOrDigit(lower[index - 1]);
            int end = index + word.Length;
            bool endOk = end >= lower.Length || !char.IsLetterOrDigit(lower[end]);
            if (startOk && endOk)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    [GeneratedRegex(@"[{};]\s*$|\w+\([^)]*\)\s*[;{]|=>|</?\w+>", RegexOptions.Multiline, matchTimeoutMilliseconds: 200)]
    private static partial Regex CodeShape();
}

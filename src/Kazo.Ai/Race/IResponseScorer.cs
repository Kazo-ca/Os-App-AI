namespace Kazo.Ai.Race;

/// <summary>
/// Gives an answer a score from 0 to 100, to pick the best answer of a race. Replaceable: a module
/// can register its own scorer (<see cref="Modules.ModuleRegistry.Scorer"/>).
/// </summary>
public interface IResponseScorer
{
    /// <summary>Scores an answer.</summary>
    /// <param name="content">The answer.</param>
    /// <param name="query">The question it answers.</param>
    /// <returns>0 (useless) to 100.</returns>
    double Score(string content, string query);
}

/// <summary>
/// The default scorer: a plain quality estimate written for this app. It rewards an answer that is
/// substantial (up to a point), organised (paragraphs, lists, headings, code blocks when the question
/// is about code) and on topic (it uses the words of the question), and penalises one that is empty,
/// cut or repeats itself.
/// </summary>
public sealed class HeuristicResponseScorer : IResponseScorer
{
    private static readonly char[] WordSeparators = [' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '"', '\'', '[', ']', '{', '}'];

    /// <summary>The one instance.</summary>
    public static HeuristicResponseScorer Instance { get; } = new();

    /// <inheritdoc />
    public double Score(string content, string query)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return 0;
        }

        string text = content.Trim();
        double score = 0;

        // Substance: grows with the length, flat after ~1500 characters (35 points).
        score += 35 * Math.Min(1, Math.Log10(1 + text.Length) / Math.Log10(1500));

        // Organisation (25 points).
        string[] lines = text.Split('\n');
        int paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        int listItems = lines.Count(line => IsListItem(line.TrimStart()));
        int headings = lines.Count(line => line.TrimStart().StartsWith('#'));
        bool hasCode = text.Contains("```", StringComparison.Ordinal);
        score += Math.Min(10, paragraphs * 2.5);
        score += Math.Min(8, listItems * 1.5);
        score += Math.Min(4, headings * 2);
        if (hasCode && LooksLikeCodeQuestion(query))
        {
            score += 3;
        }

        // On topic (25 points): share of the question's meaningful words found in the answer.
        var queryWords = Words(query).Where(word => word.Length > 3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (queryWords.Count == 0)
        {
            score += 15;
        }
        else
        {
            var answerWords = Words(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            double coverage = queryWords.Count(answerWords.Contains) / (double)queryWords.Count;
            score += 25 * coverage;
        }

        // Variety (15 points): a repetitive answer loses them.
        var all = Words(text).ToList();
        if (all.Count > 0)
        {
            double diversity = all.Distinct(StringComparer.OrdinalIgnoreCase).Count() / (double)all.Count;
            score += 15 * Math.Clamp(diversity * 1.6, 0, 1);
        }

        // A cut answer (an open code block) loses points.
        if (CountOccurrences(text, "```") % 2 == 1)
        {
            score -= 10;
        }

        return Math.Round(Math.Clamp(score, 0, 100), 1);
    }

    private static bool IsListItem(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
        || (line.Length > 2 && char.IsDigit(line[0]) && (line[1] == '.' || (line.Length > 3 && char.IsDigit(line[1]) && line[2] == '.')));

    private static bool LooksLikeCodeQuestion(string query) =>
        query.Contains("code", StringComparison.OrdinalIgnoreCase) || query.Contains("function", StringComparison.OrdinalIgnoreCase)
        || query.Contains("fonction", StringComparison.OrdinalIgnoreCase) || query.Contains("```", StringComparison.Ordinal);

    private static IEnumerable<string> Words(string text) =>
        text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

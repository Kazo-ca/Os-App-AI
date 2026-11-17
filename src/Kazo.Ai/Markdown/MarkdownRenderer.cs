using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Kazo.Ai.Markdown;

/// <summary>
/// Turns a model's Markdown answer into HTML that is safe to show: every character of the answer
/// is HTML-encoded first, and only the tags this class writes itself reach the page. Covers what
/// chat answers use: headings, paragraphs, bulleted and numbered lists, quotes, fenced code, simple
/// tables, rules, and inline code, bold, italic, strikethrough and links (http and https only).
/// </summary>
public static partial class MarkdownRenderer
{
    /// <summary>The longest text rendered as Markdown; a longer answer is shown as plain text.</summary>
    public const int MaxLength = 400_000;

    /// <summary>Renders Markdown to HTML.</summary>
    /// <param name="markdown">The Markdown.</param>
    /// <returns>The HTML.</returns>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        if (markdown.Length > MaxLength)
        {
            return "<pre class=\"md-plain\">" + WebUtility.HtmlEncode(markdown) + "</pre>";
        }

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var html = new StringBuilder(markdown.Length + 64);
        var paragraph = new List<string>();
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush(html, paragraph);
                string fence = trimmed[..3];
                string language = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }

                i++;
                string languageClass = LanguagePattern().IsMatch(language) ? " class=\"language-" + language + "\"" : string.Empty;
                html.Append("<pre class=\"md-code\"><code").Append(languageClass).Append('>')
                    .Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\n')))
                    .Append("</code></pre>");
                continue;
            }

            if (trimmed.Length == 0)
            {
                Flush(html, paragraph);
                i++;
                continue;
            }

            Match heading = HeadingPattern().Match(trimmed);
            if (heading.Success)
            {
                Flush(html, paragraph);
                int level = heading.Groups[1].Length;
                html.Append("<h").Append(level).Append('>').Append(Inline(heading.Groups[2].Value)).Append("</h").Append(level).Append('>');
                i++;
                continue;
            }

            if (RulePattern().IsMatch(trimmed))
            {
                Flush(html, paragraph);
                html.Append("<hr>");
                i++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                Flush(html, paragraph);
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    string inner = lines[i].TrimStart()[1..];
                    quote.Add(inner.StartsWith(' ') ? inner[1..] : inner);
                    i++;
                }

                html.Append("<blockquote>").Append(ToHtml(string.Join('\n', quote))).Append("</blockquote>");
                continue;
            }

            if (IsTableStart(lines, i))
            {
                Flush(html, paragraph);
                i = Table(html, lines, i);
                continue;
            }

            if (ListItem(trimmed, out bool ordered, out _))
            {
                Flush(html, paragraph);
                i = List(html, lines, i, ordered);
                continue;
            }

            paragraph.Add(trimmed);
            i++;
        }

        Flush(html, paragraph);
        return html.ToString();
    }

    /// <summary>Renders one line of inline Markdown (already free of block syntax).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The HTML.</returns>
    public static string Inline(string text)
    {
        // Inline code first, so nothing inside it is touched: each span is replaced by a marker
        // that cannot appear in the encoded text, then put back at the end.
        var codes = new List<string>();
        string withMarkers = ReplaceEach(CodeSpanPattern(), text, match =>
        {
            codes.Add("<code>" + WebUtility.HtmlEncode(match.Groups[1].Value) + "</code>");
            return "\u0001" + (codes.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0002";
        });

        string html = WebUtility.HtmlEncode(withMarkers);
        html = ReplaceEach(LinkPattern(), html, match =>
        {
            string url = WebUtility.HtmlDecode(match.Groups[2].Value);
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? "<a href=\"" + WebUtility.HtmlEncode(uri.AbsoluteUri) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + match.Groups[1].Value + "</a>"
                : match.Groups[1].Value;
        });
        html = BoldPattern().Replace(html, "<strong>$2</strong>");
        html = ItalicPattern().Replace(html, "<em>$2</em>");
        html = StrikePattern().Replace(html, "<del>$1</del>");
        return ReplaceEach(CodeMarkerPattern(), html, match =>
            int.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out int index) && index < codes.Count
                ? codes[index]
                : string.Empty);
    }

    /// <summary>
    /// Replaces each match of <paramref name="pattern"/> by what <paramref name="replace"/> returns.
    /// </summary>
    /// <remarks>
    /// Same result as <c>Regex.Replace(string, MatchEvaluator)</c>, which the trimmed framework of
    /// Kazo OS does not keep: the Store refuses an app that calls it (ADR 0242 of Kazo OS).
    /// </remarks>
    private static string ReplaceEach(Regex pattern, string input, Func<Match, string> replace)
    {
        // Regex.Matches, not Match.NextMatch() or Regex.Match(string, int): the trimmed framework
        // keeps neither. Matches never overlap and come in order.
        MatchCollection matches = pattern.Matches(input);
        if (matches.Count == 0)
        {
            return input;
        }

        var result = new StringBuilder(input.Length);
        int copied = 0;
        foreach (Match match in matches)
        {
            result.Append(input, copied, match.Index - copied).Append(replace(match));
            copied = match.Index + match.Length;
        }

        return result.Append(input, copied, input.Length - copied).ToString();
    }

    private static void Flush(StringBuilder html, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        html.Append("<p>").Append(string.Join("<br>", paragraph.Select(Inline))).Append("</p>");
        paragraph.Clear();
    }

    private static bool ListItem(string trimmed, out bool ordered, out string content)
    {
        Match match = ListPattern().Match(trimmed);
        ordered = match.Success && char.IsDigit(match.Groups[1].Value[0]);
        content = match.Success ? match.Groups[2].Value : string.Empty;
        return match.Success;
    }

    private static int List(StringBuilder html, string[] lines, int i, bool ordered)
    {
        int indent = lines[i].Length - lines[i].TrimStart().Length;
        html.Append(ordered ? "<ol>" : "<ul>");
        bool open = false;
        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            int lineIndent = line.Length - trimmed.Length;
            if (trimmed.Length == 0)
            {
                // A blank line ends the list unless the next line goes on with it.
                if (i + 1 < lines.Length && ListItem(lines[i + 1].TrimStart(), out _, out _))
                {
                    i++;
                    continue;
                }

                break;
            }

            if (ListItem(trimmed, out bool itemOrdered, out string content))
            {
                if (lineIndent > indent + 1)
                {
                    i = List(html, lines, i, itemOrdered);
                    continue;
                }

                if (lineIndent < indent || itemOrdered != ordered)
                {
                    break;
                }

                if (open)
                {
                    html.Append("</li>");
                }

                html.Append("<li>").Append(Inline(content));
                open = true;
                i++;
                continue;
            }

            if (lineIndent > indent && open)
            {
                html.Append("<br>").Append(Inline(trimmed));
                i++;
                continue;
            }

            break;
        }

        if (open)
        {
            html.Append("</li>");
        }

        html.Append(ordered ? "</ol>" : "</ul>");
        return i;
    }

    private static bool IsTableStart(string[] lines, int i) =>
        lines[i].Contains('|', StringComparison.Ordinal) &&
        i + 1 < lines.Length &&
        TableRulePattern().IsMatch(lines[i + 1].Trim());

    private static int Table(StringBuilder html, string[] lines, int i)
    {
        html.Append("<div class=\"md-table\"><table><thead><tr>");
        foreach (string cell in Cells(lines[i]))
        {
            html.Append("<th>").Append(Inline(cell)).Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        i += 2;
        while (i < lines.Length && lines[i].Contains('|', StringComparison.Ordinal) && lines[i].Trim().Length > 0)
        {
            html.Append("<tr>");
            foreach (string cell in Cells(lines[i]))
            {
                html.Append("<td>").Append(Inline(cell)).Append("</td>");
            }

            html.Append("</tr>");
            i++;
        }

        html.Append("</tbody></table></div>");
        return i;
    }

    private static IEnumerable<string> Cells(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(cell => cell.Trim());
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^([-*+]|\d{1,9}[.)])\s+(.*)$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex TableRulePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_+#-]{1,32}$")]
    private static partial Regex LanguagePattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpanPattern();

    [GeneratedRegex("\u0001(\\d+)\u0002")]
    private static partial Regex CodeMarkerPattern();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"(?<![*\w])(\*|_)(?=\S)(.+?)(?<=\S)\1(?![*\w])")]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex StrikePattern();
}

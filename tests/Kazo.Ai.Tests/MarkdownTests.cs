using Kazo.Ai.Markdown;

namespace Kazo.Ai.Tests;

public sealed class MarkdownTests
{
    [Fact]
    public void Html_in_the_answer_is_encoded()
    {
        string html = MarkdownRenderer.ToHtml("<script>alert(1)</script> <img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Only_http_links_become_links()
    {
        string html = MarkdownRenderer.ToHtml("[ok](https://kazo.ca) [bad](javascript:alert(1))");

        Assert.Contains("<a href=\"https://kazo.ca/\" target=\"_blank\" rel=\"noopener noreferrer\">ok</a>", html);
        Assert.DoesNotContain("href=\"javascript", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_text_cannot_break_out_of_the_attribute()
    {
        string html = MarkdownRenderer.ToHtml("[x](https://a.b/\"onmouseover=\"alert(1))");

        Assert.DoesNotContain("\"onmouseover", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_blocks_keep_their_text_and_language()
    {
        string html = MarkdownRenderer.ToHtml("```csharp\nvar a = 1 < 2 && **b**;\n```");

        Assert.Contains("<pre class=\"md-code\"><code class=\"language-csharp\">var a = 1 &lt; 2 &amp;&amp; **b**;</code></pre>", html);
    }

    [Fact]
    public void Inline_markup_is_rendered()
    {
        string html = MarkdownRenderer.ToHtml("**bold** *it* `code` ~~gone~~");

        Assert.Equal("<p><strong>bold</strong> <em>it</em> <code>code</code> <del>gone</del></p>", html);
    }

    [Fact]
    public void Several_code_spans_and_links_on_one_line_keep_the_text_between_them()
    {
        string html = MarkdownRenderer.ToHtml("a `x<y` b [one](https://a.b) c `z` d [two](https://c.d) e");

        Assert.Equal(
            "<p>a <code>x&lt;y</code> b <a href=\"https://a.b/\" target=\"_blank\" rel=\"noopener noreferrer\">one</a> c <code>z</code> d <a href=\"https://c.d/\" target=\"_blank\" rel=\"noopener noreferrer\">two</a> e</p>",
            html);
    }

    [Fact]
    public void Headings_lists_quotes_and_tables_are_rendered()
    {
        string html = MarkdownRenderer.ToHtml("# Title\n\n- one\n- two\n\n1. first\n2. second\n\n> quoted\n\n| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<ul><li>one</li><li>two</li></ul>", html);
        Assert.Contains("<ol><li>first</li><li>second</li></ol>", html);
        Assert.Contains("<blockquote><p>quoted</p></blockquote>", html);
        Assert.Contains("<th>a</th>", html);
        Assert.Contains("<td>2</td>", html);
    }

    [Fact]
    public void Unclosed_code_block_while_streaming_still_renders()
    {
        string html = MarkdownRenderer.ToHtml("Here:\n```js\nconst a = 1;");

        Assert.Contains("<code class=\"language-js\">const a = 1;</code>", html);
    }
}

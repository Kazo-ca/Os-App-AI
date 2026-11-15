using System.Text.Json;
using Kazo.Ai.Chat;
using Kazo.Ai.Http;
using Kazo.Ai.Providers;

namespace Kazo.Ai.Tests;

public sealed class SseParserTests
{
    [Fact]
    public void Events_split_across_chunks_are_joined()
    {
        var parser = new SseParser();

        Assert.Empty(parser.Push("data: {\"a\""));
        IReadOnlyList<string> events = parser.Push(":1}\n\ndata: [DONE]\n\n");

        Assert.Equal(["{\"a\":1}", "[DONE]"], events);
    }

    [Fact]
    public void Comments_and_other_fields_are_skipped()
    {
        var parser = new SseParser();

        IReadOnlyList<string> events = parser.Push(": OPENROUTER PROCESSING\n\nevent: x\nid: 3\ndata: hello\r\n\r\n");

        Assert.Equal(["hello"], events);
    }

    [Fact]
    public void Complete_returns_an_event_without_its_blank_line()
    {
        var parser = new SseParser();
        parser.Push("data: last");

        Assert.Equal(["last"], parser.Complete());
    }
}

public sealed class OpenAiCompatibleProviderTests
{
    private static readonly ChatRequest Request = new(
        "openai/gpt-4o-mini",
        [ChatMessage.System("Be brief."), ChatMessage.User("Hi")],
        new SamplingParameters { Temperature = 0.3, TopK = 40, RepetitionPenalty = 1.1 },
        200);

    [Fact]
    public void Body_holds_the_messages_and_parameters()
    {
        using JsonDocument body = JsonDocument.Parse(OpenAiCompatibleProvider.BuildBody(Request, extended: true));
        JsonElement root = body.RootElement;

        Assert.Equal("openai/gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Hi", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(40, root.GetProperty("top_k").GetInt32());
        Assert.Equal(1.1, root.GetProperty("repetition_penalty").GetDouble(), 3);
        Assert.Equal(200, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Plain_openai_body_leaves_out_the_extended_parameters()
    {
        using JsonDocument body = JsonDocument.Parse(OpenAiCompatibleProvider.BuildBody(Request, extended: false));

        Assert.False(body.RootElement.TryGetProperty("top_k", out _));
        Assert.False(body.RootElement.TryGetProperty("repetition_penalty", out _));
    }

    [Fact]
    public void Stream_event_gives_the_text_piece()
    {
        ChatDelta? delta = OpenAiCompatibleProvider.ParseStreamEvent("{\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}");

        Assert.Equal("Hel", delta?.Text);
    }

    [Fact]
    public void Error_inside_the_stream_is_raised()
    {
        var exception = Assert.Throws<AiServiceException>(() =>
            OpenAiCompatibleProvider.ParseStreamEvent("{\"error\":{\"message\":\"No credits\",\"code\":402}}"));

        Assert.Equal(AiErrorKind.Credits, exception.Kind);
    }

    [Fact]
    public void Completion_without_stream_is_read()
    {
        ChatDelta delta = OpenAiCompatibleProvider.ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"Hello\"},\"finish_reason\":\"stop\"}]}");

        Assert.Equal("Hello", delta.Text);
    }

    [Fact]
    public void Model_list_is_read()
    {
        IReadOnlyList<ModelInfo> models = OpenAiCompatibleProvider.ParseModels(
            "{\"data\":[{\"id\":\"a/b\",\"name\":\"B\",\"context_length\":8192},{\"id\":\"c\"}]}");

        Assert.Equal(2, models.Count);
        Assert.Equal("B", models[0].Name);
        Assert.Equal(8192, models[0].ContextLength);
        Assert.Equal("c", models[1].Name);
    }

    [Theory]
    [InlineData(401, AiErrorKind.Auth)]
    [InlineData(402, AiErrorKind.Credits)]
    [InlineData(404, AiErrorKind.NotFound)]
    [InlineData(429, AiErrorKind.RateLimit)]
    [InlineData(504, AiErrorKind.Timeout)]
    public void Http_status_gives_the_error_kind(int status, AiErrorKind kind) =>
        Assert.Equal(kind, OpenAiCompatibleProvider.Failure(status, "{\"error\":{\"message\":\"x\"}}").Kind);

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://example.com/v1", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Only_https_or_local_http_addresses_are_valid(string url, bool valid) =>
        Assert.Equal(valid, new ProviderSettings("x", "X", BaseUrl: url).HasValidBaseUrl());

    [Fact]
    public void Model_ref_parses_provider_and_model()
    {
        Assert.True(ModelRef.TryParse("openrouter:meta-llama/llama-3.1-8b:free", out ModelRef? model));
        Assert.Equal("openrouter", model!.ProviderId);
        Assert.Equal("meta-llama/llama-3.1-8b:free", model.ModelId);
        Assert.False(ModelRef.TryParse("no-provider", out _));
    }
}

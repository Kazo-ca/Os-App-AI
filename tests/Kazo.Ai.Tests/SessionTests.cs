using Kazo.Ai.Chat;
using Kazo.Ai.Http;
using Kazo.Ai.Modules;
using Kazo.Ai.Providers;
using Kazo.Ai.Settings;
using Kazo.Ai.Storage;

namespace Kazo.Ai.Tests;

public sealed class ChatSessionTests
{
    private readonly MemoryAiFileSystem _files = new();
    private readonly MemoryApiKeyStore _keys = new();
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.Ordinal);

    private async Task<ChatSession> CreateAsync(AiSettings? settings = null)
    {
        var store = new AiStore(_files, "/home/test");
        if (settings is not null)
        {
            await store.SaveSettingsAsync(settings);
        }

        var session = new ChatSession(store, _keys, _ => _providers, ModuleRegistry.CreateDefault());
        await session.InitializeAsync();
        return session;
    }

    [Fact]
    public async Task Message_is_answered_saved_and_titled()
    {
        _providers["openrouter"] = FakeProvider.Fixed("openrouter", "Hello", " there");
        await _keys.SetAsync("openrouter", "OpenRouter", "sk-test");
        ChatSession session = await CreateAsync();

        await session.SendAsync("Say hello please");

        Conversation conversation = Assert.Single(session.Conversations);
        Assert.Equal("Say hello please", conversation.Title);
        ConversationMessage answer = conversation.Messages[^1];
        Assert.Equal("Hello there", answer.Content);
        Assert.Equal("openrouter:openai/gpt-4o-mini", answer.Model);
        Assert.NotNull(answer.AutoTune);
        Assert.False(session.IsBusy);
        Assert.Single(await new AiStore(_files, "/home/test").LoadConversationsAsync());
    }

    [Fact]
    public async Task Persona_prompt_is_the_system_message()
    {
        var provider = FakeProvider.Fixed("openrouter", "ok");
        _providers["openrouter"] = provider;
        await _keys.SetAsync("openrouter", "OpenRouter", "sk-test");
        ChatSession session = await CreateAsync(new AiSettings { PersonaId = "developer" });

        await session.SendAsync("hi");

        Assert.Contains("software engineer", provider.Requests.Single().SystemText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_key_the_local_model_answers()
    {
        _providers["openrouter"] = FakeProvider.Fixed("openrouter", "remote");
        _providers[ProviderSettings.LocalId] = FakeProvider.Fixed(ProviderSettings.LocalId, "local");
        ChatSession session = await CreateAsync();

        await session.SendAsync("hi");

        Assert.Equal("local", session.Current!.Messages[^1].Content);
        Assert.StartsWith("local:", session.Current.Messages[^1].Model, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_is_kept_with_its_kind()
    {
        _providers["openrouter"] = new FakeProvider("openrouter", _ => []) { Failure = new AiServiceException(AiErrorKind.Auth, "Invalid key", 401) };
        await _keys.SetAsync("openrouter", "OpenRouter", "sk-bad");
        ChatSession session = await CreateAsync();

        await session.SendAsync("hi");

        ConversationMessage answer = session.Current!.Messages[^1];
        Assert.Equal("Auth", answer.ErrorKind);
        Assert.Equal("Invalid key", answer.Error);
    }

    [Fact]
    public async Task Stop_keeps_what_was_written()
    {
        _providers["openrouter"] = new FakeProvider("openrouter", _ => ["one ", "two ", "three"]) { Delay = TimeSpan.FromMilliseconds(80) };
        await _keys.SetAsync("openrouter", "OpenRouter", "sk-test");
        ChatSession session = await CreateAsync();

        Task sending = session.SendAsync("count");
        while (session.PendingText.Length == 0)
        {
            await Task.Delay(10);
        }

        session.Stop();
        await sending;

        ConversationMessage answer = session.Current!.Messages[^1];
        Assert.True(answer.Stopped);
        Assert.StartsWith("one", answer.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Race_mode_keeps_the_winner_and_the_ranking()
    {
        _providers["a"] = FakeProvider.Fixed("a", "short");
        _providers["b"] = FakeProvider.Fixed("b", "## Why\n\n", "- productivity rises over time\n", "- investment adds capital and output\n");
        ChatSession session = await CreateAsync(new AiSettings
        {
            Providers = [ProviderPresets.Local, new ProviderSettings("a", "A", BaseUrl: "https://a.example/v1"), new ProviderSettings("b", "B", BaseUrl: "https://b.example/v1")],
            Mode = ChatMode.Race,
            RaceModels = ["a:m1", "b:m2"],
        });

        await session.SendAsync("why does the economy grow?");

        ConversationMessage answer = session.Current!.Messages[^1];
        Assert.Equal("b:m2", answer.Model);
        Assert.Equal(2, answer.Race!.ModelsQueried);
    }

    [Fact]
    public async Task Ratings_teach_autotune_and_are_saved()
    {
        _providers["openrouter"] = FakeProvider.Fixed("openrouter", "Answer");
        await _keys.SetAsync("openrouter", "OpenRouter", "sk-test");
        ChatSession session = await CreateAsync();
        await session.SendAsync("hi");
        string id = session.Current!.Messages[^1].Id;

        await session.RateAsync(id, 1);

        Assert.Equal(1, session.Current.Messages[^1].Rating);
        Assert.Single(session.Feedback.History);
        Assert.Contains("/home/test/.config/ca.kazo.ai/feedback.json", _files.Files.Keys);
    }

    [Fact]
    public async Task Regenerate_replaces_the_last_answer()
    {
        int calls = 0;
        _providers["openrouter"] = new FakeProvider("openrouter", _ => [(++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        await _keys.SetAsync("openrouter", "OpenRouter", "sk-test");
        ChatSession session = await CreateAsync();
        await session.SendAsync("hi");

        await session.RegenerateAsync();

        Assert.Equal(2, session.Current!.Messages.Count);
        Assert.Equal("2", session.Current.Messages[^1].Content);
    }

    [Fact]
    public async Task Json_module_with_a_taken_id_is_refused()
    {
        _files.Files["/home/test/.config/ca.kazo.ai/modules/concise.json"] = "{\"id\":\"concise\",\"name\":\"Mine\"}";

        ChatSession session = await CreateAsync();

        Assert.Single(session.ModuleErrors);
        Assert.Equal("built-in", session.Modules.OriginOf("concise"));
    }
}

public sealed class ManifestTests
{
    [Fact]
    public void Manifest_declares_the_app_and_its_capabilities()
    {
        var app = new AiApp();

        Assert.Equal("ca.kazo.ai", app.Id);
        Assert.Equal(typeof(AiWindow), app.EntryPoint);
        Assert.Contains(Kazo.Os.App.Abstractions.AppCapabilities.NetworkRealAccess, app.Capabilities);
        Assert.Contains(Kazo.Os.App.Abstractions.AppCapabilities.FileSystemPrivateWrite, app.Capabilities);
        Assert.True(app.SingleInstancePerUser);
    }
}

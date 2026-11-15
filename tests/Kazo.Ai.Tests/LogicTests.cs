using Kazo.Ai.Chat;
using Kazo.Ai.Modules;
using Kazo.Ai.Providers;
using Kazo.Ai.Race;
using Kazo.Ai.Settings;
using Kazo.Ai.Storage;
using Kazo.Ai.Tuning;

namespace Kazo.Ai.Tests;

public sealed class AutoTuneTests
{
    [Fact]
    public void Code_question_is_detected_and_cools_the_temperature()
    {
        AutoTuneResult result = AutoTuner.Tune(
            [ChatMessage.User("Fix this C# function: `public int Add(int a, int b) { return a - b; }` it throws an exception")],
            AutoTuneStrategy.Adaptive);

        Assert.Equal(ContextType.Code, result.DetectedContext);
        Assert.True(result.Params.Temperature < 0.6);
    }

    [Fact]
    public void Creative_request_warms_the_temperature()
    {
        AutoTuneResult result = AutoTuner.Tune(
            [ChatMessage.User("Écris un poème imaginatif sur une histoire de dragons et de rêves")],
            AutoTuneStrategy.Adaptive);

        Assert.Equal(ContextType.Creative, result.DetectedContext);
        Assert.True(result.Params.Temperature > 0.8);
    }

    [Fact]
    public void Manual_strategy_keeps_the_hand_set_parameters()
    {
        var manual = new SamplingParameters { Temperature = 0.42 };

        AutoTuneResult result = AutoTuner.Tune([ChatMessage.User("hello")], AutoTuneStrategy.Manual, manual);

        Assert.Equal(0.42, result.Params.Temperature, 3);
        Assert.Empty(result.ParamDeltas);
    }

    [Fact]
    public void Parameters_stay_in_their_ranges()
    {
        SamplingParameters clamped = new SamplingParameters { Temperature = 9, TopP = -1, TopK = 5000 }.Clamp();

        Assert.InRange(clamped.Temperature, 0, 2);
        Assert.InRange(clamped.TopP, 0, 1);
        Assert.InRange(clamped.TopK, 0, 1000);
    }

    [Fact]
    public void Context_scores_add_up_to_one_hundred()
    {
        IReadOnlyList<ContextScore> scores = ContextDetector.Score([ChatMessage.User("why does the economy grow?")]);

        Assert.Equal(100, scores.Sum(score => score.Percentage), 0);
    }

    [Fact]
    public void Feedback_needs_a_few_ratings_before_it_changes_anything()
    {
        FeedbackState state = FeedbackState.Empty;
        SamplingParameters baseline = SamplingParameters.Default;
        for (int i = 0; i < 2; i++)
        {
            state = FeedbackLearner.Record(state, Rating(i, 1, 0.2));
        }

        Assert.Equal(baseline, FeedbackLearner.Apply(baseline, ContextType.Code, state));

        for (int i = 2; i < 8; i++)
        {
            state = FeedbackLearner.Record(state, Rating(i, 1, 0.2));
        }

        Assert.True(FeedbackLearner.Apply(baseline, ContextType.Code, state).Temperature < baseline.Temperature);
    }

    private static FeedbackRecord Rating(int index, int rating, double temperature) => new(
        "m" + index,
        index,
        ContextType.Code,
        "test:model",
        "assistant",
        SamplingParameters.Default with { Temperature = temperature },
        rating,
        FeedbackLearner.Measure("Some answer."));
}

public sealed class ModelRaceTests
{
    [Fact]
    public async Task Best_answer_wins_and_failures_are_reported()
    {
        var good = FakeProvider.Fixed("good", "## Answer\n\n", "- the economy grows when productivity rises\n", "- because investment and trade increase output\n");
        var short_ = FakeProvider.Fixed("short", "ok");
        var broken = new FakeProvider("broken", _ => []) { Failure = new Http.AiServiceException(Http.AiErrorKind.Auth, "bad key", 401) };
        var providers = new Dictionary<string, IChatProvider> { ["good"] = good, ["short"] = short_, ["broken"] = broken };
        var race = new ModelRace(id => providers.GetValueOrDefault(id), HeuristicResponseScorer.Instance);
        var results = new List<RaceModelResult>();

        RaceResult result = await race.RunAsync(
            [new ModelRef("good", "g"), new ModelRef("short", "s"), new ModelRef("broken", "b")],
            new ChatRequest("x", [ChatMessage.User("why does the economy grow?")], SamplingParameters.Default),
            new RaceCallbacks { OnModelResult = results.Add });

        Assert.Equal("good:g", result.Winner?.Model);
        Assert.Equal(3, result.Race.ModelsQueried);
        Assert.Equal(2, result.Race.ModelsSucceeded);
        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => !r.Success && r.ErrorKind == Http.AiErrorKind.Auth);
        Assert.Equal("g", good.Requests.Single().Model);
    }

    [Fact]
    public async Task Race_with_every_model_failing_has_no_winner()
    {
        var broken = new FakeProvider("broken", _ => []) { Failure = new Http.AiServiceException(Http.AiErrorKind.RateLimit, "slow down", 429) };
        var race = new ModelRace(_ => broken, HeuristicResponseScorer.Instance);
        var results = new List<RaceModelResult>();

        RaceResult result = await race.RunAsync(
            [new ModelRef("broken", "a"), new ModelRef("broken", "b")],
            new ChatRequest("x", [ChatMessage.User("hi")], SamplingParameters.Default),
            new RaceCallbacks { OnModelResult = results.Add });

        Assert.Null(result.Winner);
        Assert.Equal(Http.AiErrorKind.RateLimit, ModelRace.CommonFailure(results));
    }
}

public sealed class ModuleTests
{
    private const string ModuleJson = """
        {
          "id": "pirate_voice",
          "name": "Pirate voice",
          "description": "Answers like a pirate.",
          "version": "1.0.0",
          "author": "test",
          "systemPrompt": "Talk like a pirate.",
          "replacements": [ { "pattern": "hello", "replacement": "ahoy", "flags": "i" } ],
          "params": { "temperature": 1.2 }
        }
        """;

    [Fact]
    public async Task Json_module_changes_the_request_and_the_answer()
    {
        DeclarativeModule module = DeclarativeModule.Parse(ModuleJson);
        var request = new ChatRequest("m", [ChatMessage.System("Be kind."), ChatMessage.User("hi")], SamplingParameters.Default);

        ChatRequest changed = await module.TransformRequestAsync(request, new ModuleContext("c", "p", "x", new Dictionary<string, string>()), CancellationToken.None);

        Assert.Equal("Be kind.\n\nTalk like a pirate.", changed.SystemText);
        Assert.Equal(1.2, changed.Parameters.Temperature, 3);
        Assert.Equal("ahoy there, ahoy? ahoy", module.TransformResponse("Hello there, HELLO? hello", null));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"Bad Id\"}")]
    [InlineData("{\"id\":\"ok\",\"systemPromptPosition\":\"middle\"}")]
    [InlineData("{\"id\":\"ok\",\"params\":{\"unknown\":1}}")]
    [InlineData("{\"id\":\"ok\",\"replacements\":[{\"pattern\":\"(\",\"replacement\":\"\"}]}")]
    [InlineData("not json")]
    public void Invalid_module_is_refused_with_a_reason(string json) =>
        Assert.Throws<FormatException>(() => DeclarativeModule.Parse(json));

    [Fact]
    public async Task Registry_runs_only_the_enabled_modules_in_order()
    {
        ModuleRegistry registry = ModuleRegistry.CreateDefault();
        var request = new ChatRequest("m", [ChatMessage.User("hi")], SamplingParameters.Default);

        (ChatRequest changed, IReadOnlyList<string> applied) = await registry.TransformRequestAsync(
            request,
            ["same_language", "concise"],
            new ModuleContext("c", "p", "x", new Dictionary<string, string>()),
            null,
            CancellationToken.None);

        Assert.Equal(["concise", "same_language"], applied);
        Assert.NotEmpty(changed.SystemText);
    }

    [Fact]
    public void Whitespace_tidier_leaves_code_blocks_alone()
    {
        IChatModule tidier = ModuleRegistry.CreateDefault().Find("whitespace_tidier")!;

        string tidied = tidier.TransformResponse("Text   \n\n\n\nMore\n```\na   \n\n\n\nb\n```", null);

        Assert.Equal("Text\n\nMore\n```\na   \n\n\n\nb\n```", tidied);
    }
}

public sealed class StoreTests
{
    [Fact]
    public async Task Settings_conversations_feedback_and_modules_round_trip()
    {
        var files = new MemoryAiFileSystem();
        var store = new AiStore(files, "/home/test");
        AiSettings settings = new AiSettings { Mode = ChatMode.Race, Strategy = AutoTuneStrategy.Creative }.Normalize();
        var conversation = new Conversation
        {
            Title = "Hello",
            Messages = [new ConversationMessage { Role = ChatRole.User, Content = "hi" }],
        };

        await store.SaveSettingsAsync(settings);
        await store.SaveConversationAsync(conversation);
        await store.SaveModuleAsync(DeclarativeModule.Parse("{\"id\":\"my_mod\",\"name\":\"Mine\",\"systemPrompt\":\"x\"}"));

        Assert.Contains("/home/test/.config/ca.kazo.ai/settings.json", files.Files.Keys);
        Assert.Equal(ChatMode.Race, (await store.LoadSettingsAsync()).Mode);
        Assert.Equal("Hello", (await store.LoadConversationsAsync()).Single().Title);
        Assert.Equal("my_mod", (await store.LoadModulesAsync()).Modules.Single().Id);
    }

    [Fact]
    public async Task Broken_settings_file_gives_the_defaults()
    {
        var files = new MemoryAiFileSystem();
        files.Files["/home/test/.config/ca.kazo.ai/settings.json"] = "{ not json";

        AiSettings settings = await new AiStore(files, "/home/test").LoadSettingsAsync();

        Assert.Equal(new AiSettings().SelectedModel, settings.SelectedModel);
    }

    [Fact]
    public void Normalize_always_keeps_the_local_provider()
    {
        AiSettings settings = new AiSettings { Providers = [ProviderPresets.OpenRouter] }.Normalize();

        Assert.NotNull(settings.FindProvider(ProviderSettings.LocalId));
    }

    [Fact]
    public void Export_round_trips_without_any_key()
    {
        var settings = new AiSettings { PersonaId = "developer" };
        DeclarativeModule module = DeclarativeModule.Parse("{\"id\":\"my_mod\",\"name\":\"Mine\",\"systemPrompt\":\"x\"}");

        string json = SettingsExport.Write(settings, [module], DateTimeOffset.UnixEpoch);
        (AiSettings read, IReadOnlyList<DeclarativeModule> modules) = SettingsExport.Read(json);

        Assert.Equal("developer", read.PersonaId);
        Assert.Equal("my_mod", modules.Single().Id);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Something_else_than_an_export_is_refused() =>
        Assert.Throws<FormatException>(() => SettingsExport.Read("{\"format\":\"other\"}"));
}

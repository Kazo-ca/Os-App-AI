using Kazo.Ai.Http;
using Kazo.Ai.Modules;
using Kazo.Ai.Providers;
using Kazo.Ai.Race;
using Kazo.Ai.Settings;
using Kazo.Ai.Storage;
using Kazo.Ai.Tuning;

namespace Kazo.Ai.Chat;

/// <summary>
/// The chat itself, without its window: the conversations, the settings, sending a message in
/// single mode or as a race, AutoTune, the modules and the ratings. The window shows its state and
/// calls its methods; the tests drive it with fake providers and an in-memory file system.
/// </summary>
/// <remarks>
/// One message is answered at a time. Every change raises <see cref="Changed"/>, which may come
/// from a background continuation: the window marshals it to its renderer.
/// </remarks>
public sealed class ChatSession
{
    /// <summary>The most conversations kept in the list.</summary>
    public const int MaxConversations = 300;

    private readonly AiStore _store;
    private readonly IApiKeyStore _keys;
    private readonly Func<Func<AiSettings>, IReadOnlyDictionary<string, IChatProvider>> _providerFactory;
    private readonly TimeProvider _time;
    private readonly List<Conversation> _conversations = [];
    private readonly Dictionary<string, string> _raceText = new(StringComparer.Ordinal);
    private readonly List<RaceModelResult> _raceResults = [];
    private IReadOnlyDictionary<string, IChatProvider> _providers = new Dictionary<string, IChatProvider>();
    private CancellationTokenSource? _running;

    /// <summary>Creates the session.</summary>
    /// <param name="store">Where the settings, conversations, ratings and modules are kept.</param>
    /// <param name="keys">The API keys (to know whether a service has one).</param>
    /// <param name="providerFactory">Makes the providers of the settings.</param>
    /// <param name="modules">The modules (the built-in and compiled-in ones; the JSON modules are added on load).</param>
    /// <param name="time">The clock.</param>
    public ChatSession(
        AiStore store,
        IApiKeyStore keys,
        Func<Func<AiSettings>, IReadOnlyDictionary<string, IChatProvider>> providerFactory,
        ModuleRegistry modules,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        Modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when anything shown changes.</summary>
    public event Action? Changed;

    /// <summary>Gets the settings.</summary>
    public AiSettings Settings { get; private set; } = new();

    /// <summary>Gets what the ratings taught AutoTune.</summary>
    public FeedbackState Feedback { get; private set; } = FeedbackState.Empty;

    /// <summary>Gets the modules.</summary>
    public ModuleRegistry Modules { get; }

    /// <summary>Gets the JSON modules found in the modules folder.</summary>
    public IReadOnlyList<DeclarativeModule> FileModules { get; private set; } = [];

    /// <summary>Gets the problems met while reading the modules folder (one line each).</summary>
    public IReadOnlyList<string> ModuleErrors { get; private set; } = [];

    /// <summary>Gets the conversations, the most recent first.</summary>
    public IReadOnlyList<Conversation> Conversations => _conversations;

    /// <summary>Gets the open conversation, or null for a new one not started yet.</summary>
    public Conversation? Current { get; private set; }

    /// <summary>Gets whether an answer is being written.</summary>
    public bool IsBusy => _running is not null;

    /// <summary>Gets the answer being written (single mode), or the leader's (race).</summary>
    public string PendingText { get; private set; } = string.Empty;

    /// <summary>Gets the model writing the pending answer.</summary>
    public string? PendingModel { get; private set; }

    /// <summary>Gets the AutoTune decision of the pending answer.</summary>
    public AutoTuneResult? PendingTune { get; private set; }

    /// <summary>Gets the text each racing model wrote so far, by model.</summary>
    public IReadOnlyDictionary<string, string> RaceText => _raceText;

    /// <summary>Gets the models of the race that finished, in the order they did.</summary>
    public IReadOnlyList<RaceModelResult> RaceResults => _raceResults;

    /// <summary>Gets the number of models in the running race (0 when none).</summary>
    public int RaceTotal { get; private set; }

    /// <summary>Gets the current leader of the race.</summary>
    public RaceLeader? RaceLeader { get; private set; }

    /// <summary>Gets the providers of the settings, by id.</summary>
    public IReadOnlyDictionary<string, IChatProvider> Providers => _providers;

    /// <summary>Reads the settings, ratings, conversations and modules.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Settings = await _store.LoadSettingsAsync(cancellationToken);
        Feedback = await _store.LoadFeedbackAsync(cancellationToken);
        _conversations.Clear();
        _conversations.AddRange(await _store.LoadConversationsAsync(cancellationToken));
        await ReloadModulesAsync(cancellationToken);
        _providers = _providerFactory(() => Settings);
        Raise();
    }

    /// <summary>Reads the modules folder again.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task ReloadModulesAsync(CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<DeclarativeModule> modules, IReadOnlyList<string> errors) = await _store.LoadModulesAsync(cancellationToken);
        Modules.RemoveOrigin(DeclarativeModule.Origin);
        var accepted = new List<DeclarativeModule>();
        var problems = new List<string>(errors);
        foreach (DeclarativeModule module in modules)
        {
            string? origin = Modules.OriginOf(module.Id);
            if (origin is not null)
            {
                problems.Add($"{module.Id}: the id is already used by a {origin} module.");
                continue;
            }

            Modules.Register(module, DeclarativeModule.Origin);
            accepted.Add(module);
        }

        FileModules = accepted;
        ModuleErrors = problems;
        Raise();
    }

    /// <summary>Saves a JSON module in the modules folder and loads it.</summary>
    /// <param name="module">The module.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task AddModuleAsync(DeclarativeModule module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        string? origin = Modules.OriginOf(module.Id);
        if (origin is not null && origin != DeclarativeModule.Origin)
        {
            throw new InvalidOperationException($"The id {module.Id} is already used by a {origin} module.");
        }

        await _store.SaveModuleAsync(module, cancellationToken);
        await ReloadModulesAsync(cancellationToken);
    }

    /// <summary>Deletes a JSON module.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task DeleteModuleAsync(string id, CancellationToken cancellationToken = default)
    {
        await _store.DeleteModuleAsync(id, cancellationToken);
        await UpdateSettingsAsync(Settings with { EnabledModules = [.. Settings.EnabledModules.Where(m => m != id)] }, cancellationToken);
        await ReloadModulesAsync(cancellationToken);
    }

    /// <summary>Replaces the settings, saves them and makes the providers again.</summary>
    /// <param name="settings">The new settings.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task UpdateSettingsAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings.Normalize();
        _providers = _providerFactory(() => Settings);
        await _store.SaveSettingsAsync(Settings, cancellationToken);
        Raise();
    }

    /// <summary>Forgets everything AutoTune learned from the ratings.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task ResetFeedbackAsync(CancellationToken cancellationToken = default)
    {
        Feedback = FeedbackState.Empty;
        await _store.SaveFeedbackAsync(Feedback, cancellationToken);
        Raise();
    }

    /// <summary>Starts a new conversation (saved with its first message).</summary>
    public void NewConversation()
    {
        if (IsBusy)
        {
            return;
        }

        Current = null;
        Raise();
    }

    /// <summary>Opens a conversation.</summary>
    /// <param name="id">Its id.</param>
    public void Select(string id)
    {
        if (IsBusy)
        {
            return;
        }

        Current = _conversations.FirstOrDefault(conversation => conversation.Id == id) ?? Current;
        Raise();
    }

    /// <summary>Deletes a conversation.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task DeleteConversationAsync(string id, CancellationToken cancellationToken = default)
    {
        if (IsBusy && Current?.Id == id)
        {
            return;
        }

        _conversations.RemoveAll(conversation => conversation.Id == id);
        if (Current?.Id == id)
        {
            Current = null;
        }

        await _store.DeleteConversationAsync(id, cancellationToken);
        Raise();
    }

    /// <summary>Renames a conversation.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">The new title.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task RenameConversationAsync(string id, string title, CancellationToken cancellationToken = default)
    {
        Conversation? conversation = _conversations.FirstOrDefault(c => c.Id == id);
        if (conversation is null || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        await StoreAsync(conversation with { Title = title.Trim() }, cancellationToken);
    }

    /// <summary>Stops the answer being written; what was written so far is kept.</summary>
    public void Stop() => _running?.Cancel();

    /// <summary>
    /// Chooses the model of the next answer in single mode: the selected one, or the local model
    /// when the selected service needs a key that was never given and the settings prefer it.
    /// </summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The model.</returns>
    public async Task<ModelRef> ResolveModelAsync(CancellationToken cancellationToken = default)
    {
        ModelRef selected = ModelRef.TryParse(Settings.SelectedModel, out ModelRef? parsed) && parsed is not null
            ? parsed
            : new ModelRef(ProviderSettings.LocalId, Settings.LocalModel);
        if (!Settings.PreferLocalWithoutKey || selected.ProviderId == ProviderSettings.LocalId)
        {
            return selected;
        }

        ProviderSettings? provider = Settings.FindProvider(selected.ProviderId);
        bool usable = provider is { Enabled: true } &&
            (!provider.NeedsKey || !string.IsNullOrEmpty(await _keys.GetAsync(provider.Id, cancellationToken)));
        return usable || !_providers.ContainsKey(ProviderSettings.LocalId)
            ? selected
            : new ModelRef(ProviderSettings.LocalId, Settings.LocalModel);
    }

    /// <summary>Sends a message and writes the answer (single mode or race, per the settings).</summary>
    /// <param name="text">The message.</param>
    /// <returns>A task that ends with the answer.</returns>
    public Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsBusy)
        {
            return Task.CompletedTask;
        }

        DateTimeOffset now = _time.GetUtcNow();
        Conversation conversation = Current ?? new Conversation
        {
            Title = Conversation.TitleFrom(text),
            PersonaId = Settings.PersonaId,
            CreatedAt = now,
        };
        conversation = conversation with
        {
            UpdatedAt = now,
            Messages = [.. conversation.Messages, new ConversationMessage { Role = ChatRole.User, Content = text.Trim(), CreatedAt = now }],
        };
        return AnswerAsync(conversation);
    }

    /// <summary>Writes the last answer again (the last assistant message is replaced).</summary>
    /// <returns>A task that ends with the answer.</returns>
    public Task RegenerateAsync()
    {
        if (IsBusy || Current is not { Messages.Count: > 0 } conversation)
        {
            return Task.CompletedTask;
        }

        int last = conversation.Messages.Count - 1;
        if (conversation.Messages[last].Role == ChatRole.Assistant)
        {
            conversation = conversation with { Messages = [.. conversation.Messages.Take(last)] };
        }

        return conversation.Messages.Count > 0 && conversation.Messages[^1].Role == ChatRole.User
            ? AnswerAsync(conversation)
            : Task.CompletedTask;
    }

    /// <summary>Rates an answer (+1 or -1; 0 removes the rating) and lets AutoTune learn from it.</summary>
    /// <param name="messageId">The answer.</param>
    /// <param name="rating">+1, -1 or 0.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task RateAsync(string messageId, int rating, CancellationToken cancellationToken = default)
    {
        if (Current is not { } conversation)
        {
            return;
        }

        int index = conversation.Messages.ToList().FindIndex(message => message.Id == messageId);
        if (index < 0 || conversation.Messages[index].Role != ChatRole.Assistant)
        {
            return;
        }

        ConversationMessage message = conversation.Messages[index];
        int value = Math.Sign(rating);
        ConversationMessage rated = message with { Rating = value == 0 ? null : value };
        var messages = conversation.Messages.ToList();
        messages[index] = rated;
        await StoreAsync(conversation with { Messages = messages }, cancellationToken);

        if (value != 0 && message.Rating != value && message.AutoTune is { } tune && Settings.LearnFromFeedback)
        {
            Feedback = FeedbackLearner.Record(Feedback, new FeedbackRecord(
                message.Id,
                _time.GetUtcNow().ToUnixTimeMilliseconds(),
                tune.DetectedContext,
                message.Model ?? string.Empty,
                conversation.PersonaId,
                tune.Params,
                value,
                FeedbackLearner.Measure(message.Content)));
            await _store.SaveFeedbackAsync(Feedback, cancellationToken);
        }

        Raise();
    }

    private async Task AnswerAsync(Conversation conversation)
    {
        using var running = new CancellationTokenSource();
        _running = running;
        CancellationToken cancellationToken = running.Token;
        Current = conversation;
        PendingText = string.Empty;
        PendingModel = null;
        ResetRace();
        Raise();

        long started = _time.GetTimestamp();
        Persona persona = Settings.FindPersona(conversation.PersonaId);
        IReadOnlyList<ChatMessage> messages = conversation.ToChatMessages(persona.SystemPrompt);
        AutoTuneResult tune = AutoTuner.Tune(messages, Settings.Strategy, Settings.ManualParams, Settings.LearnFromFeedback ? Feedback : null);
        PendingTune = tune;
        ConversationMessage answer = new() { Role = ChatRole.Assistant, CreatedAt = _time.GetUtcNow(), AutoTune = tune };

        try
        {
            bool race = Settings.Mode == ChatMode.Race && Settings.RaceModels.Count > 1;
            ModelRef single = await ResolveModelAsync(cancellationToken);
            string providerId = race ? "race" : single.ProviderId;
            var request = new ChatRequest(single.ModelId, messages, tune.Params, Settings.MaxTokens > 0 ? Settings.MaxTokens : null);
            (request, IReadOnlyList<string> requestModules) = await Modules.TransformRequestAsync(
                request,
                Settings.EnabledModules,
                new ModuleContext(conversation.Id, persona.Id, providerId, new Dictionary<string, string>()),
                Settings.ModuleConfigs,
                cancellationToken);

            answer = race
                ? await RaceAsync(request, answer, cancellationToken)
                : await SingleAsync(single, request, answer, cancellationToken);
            answer = answer with { ModulesApplied = [.. requestModules, .. answer.ModulesApplied] };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            answer = answer with { Content = PendingText, Model = PendingModel, Stopped = true };
        }
        catch (AiServiceException exception)
        {
            answer = answer with { Content = PendingText, Model = PendingModel, Error = exception.Message, ErrorKind = exception.Kind.ToString() };
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException or System.Text.Json.JsonException)
        {
            answer = answer with { Content = PendingText, Model = PendingModel, Error = exception.Message, ErrorKind = AiErrorKind.Unknown.ToString() };
        }

        answer = answer with { DurationMs = (long)_time.GetElapsedTime(started).TotalMilliseconds };
        conversation = conversation with { UpdatedAt = _time.GetUtcNow(), Messages = [.. conversation.Messages, answer] };
        _running = null;
        PendingText = string.Empty;
        PendingModel = null;
        PendingTune = null;
        RaceTotal = 0;
        try
        {
            await StoreAsync(conversation, CancellationToken.None);
        }
        catch (IOException)
        {
            // The answer stays on screen; the next save writes it.
            Current = conversation;
            Raise();
        }
    }

    private async Task<ConversationMessage> SingleAsync(ModelRef model, ChatRequest request, ConversationMessage answer, CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(model.ProviderId, out IChatProvider? provider))
        {
            throw new AiServiceException(AiErrorKind.Unavailable, $"The service {model.ProviderId} is not enabled.");
        }

        PendingModel = model.ToString();
        var text = new System.Text.StringBuilder();
        await foreach (ChatDelta delta in provider.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            if (delta.Text.Length == 0)
            {
                continue;
            }

            text.Append(delta.Text);
            PendingText = text.ToString();
            Raise();
        }

        (string response, IReadOnlyList<string> applied) = Modules.TransformResponse(text.ToString(), Settings.EnabledModules, Settings.ModuleConfigs);
        return answer with { Content = response, Model = model.ToString(), ModulesApplied = applied };
    }

    private async Task<ConversationMessage> RaceAsync(ChatRequest request, ConversationMessage answer, CancellationToken cancellationToken)
    {
        var models = Settings.RaceModels
            .Select(text => ModelRef.TryParse(text, out ModelRef? model) ? model : null)
            .OfType<ModelRef>()
            .ToList();
        var race = new ModelRace(id => _providers.GetValueOrDefault(id), Modules.Scorer, _time) { MinLeaderDelta = Settings.RaceMinDelta };
        object gate = new();
        var callbacks = new RaceCallbacks
        {
            OnRaceStart = total => { RaceTotal = total; Raise(); },
            OnModelText = (model, text) =>
            {
                lock (gate)
                {
                    _raceText[model.ToString()] = text;
                }

                Raise();
            },
            OnModelResult = result =>
            {
                lock (gate)
                {
                    _raceResults.Add(result);
                }

                Raise();
            },
            OnLeaderChange = leader =>
            {
                RaceLeader = leader;
                PendingModel = leader.Model;
                PendingText = leader.Content;
                Raise();
            },
        };

        IReadOnlyList<string> applied = [];
        RaceResult result = await race.RunAsync(models, request, callbacks, text =>
        {
            (string response, IReadOnlyList<string> modules) = Modules.TransformResponse(text, Settings.EnabledModules, Settings.ModuleConfigs);
            applied = modules;
            return response;
        }, cancellationToken);

        if (result.Winner is null)
        {
            AiErrorKind kind = ModelRace.CommonFailure(_raceResults);
            throw new AiServiceException(kind, "No model of the race answered.");
        }

        return answer with { Content = result.Response, Model = result.Winner.Model, Race = result.Race, ModulesApplied = applied };
    }

    private async Task StoreAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        _conversations.RemoveAll(c => c.Id == conversation.Id);
        _conversations.Insert(0, conversation);
        if (_conversations.Count > MaxConversations)
        {
            _conversations.RemoveRange(MaxConversations, _conversations.Count - MaxConversations);
        }

        Current = conversation;
        Raise();
        await _store.SaveConversationAsync(conversation, cancellationToken);
    }

    private void ResetRace()
    {
        _raceText.Clear();
        _raceResults.Clear();
        RaceTotal = 0;
        RaceLeader = null;
    }

    private void Raise() => Changed?.Invoke();
}

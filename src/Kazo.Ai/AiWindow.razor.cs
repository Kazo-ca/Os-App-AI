using System.Globalization;
using System.Text;
using Kazo.Ai.Chat;
using Kazo.Ai.Http;
using Kazo.Ai.Modules;
using Kazo.Ai.Providers;
using Kazo.Ai.Settings;
using Kazo.Ai.Storage;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Blazor.Interop;
using Kazo.Os.AppSdk.Clipboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Ai;

/// <summary>
/// The Kazo AI window: the conversations on the left (a side panel when the window is narrow),
/// the open conversation with its input box, and the settings over it. The chat itself is
/// <see cref="ChatSession"/>; this class wires it to the OS (files, keyring, network, clipboard)
/// and to the local model, and keeps the screen in step with it.
/// </summary>
public sealed partial class AiWindow : IAsyncDisposable
{
    // A streamed answer changes many times a second: the screen follows at most this often.
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(60);

    private ChatSession? _session;
    private BrowserStreamingHttp? _http;
    private WebLlmEngine? _local;
    private IApiKeyStore? _keys;
    private BrowserFileDownloader? _downloader;
    private BrowserFilePicker? _picker;
    private IStringLocalizer<Strings>? _localizer;
    private string _input = string.Empty;
    private string? _status;
    private bool _statusIsError;
    private bool _sidebarOpen;
    private bool _settingsOpen;
    private bool _renderQueued;
    private bool _composerBound;
    private DateTimeOffset _lastRender;
    private double? _localProgress;
    private string _localProgressText = string.Empty;
    private LocalGpuInfo? _gpu;
    private ElementReference _messages;
    private ElementReference _inputBox;
    private IJSObjectReference? _module;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private ChatSession Session => _session ?? throw new InvalidOperationException("The window is not initialized.");

    private Conversation? Current => _session?.Current;

    private bool Busy => _session?.IsBusy == true;

    private bool HasNetwork => AppContext.GrantedCapabilities.Contains(Kazo.Os.App.Abstractions.AppCapabilities.NetworkRealAccess);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            _session.Stop();
        }

        if (_local is not null)
        {
            _local.LoadProgress -= OnLocalProgress;
            await _local.DisposeAsync();
        }

        if (_http is not null)
        {
            await _http.DisposeAsync();
        }

        if (_downloader is not null)
        {
            await _downloader.DisposeAsync();
        }

        if (_picker is not null)
        {
            await _picker.DisposeAsync();
        }

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone; nothing to release.
            }
        }
    }

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        _http = new BrowserStreamingHttp(JS, AppContext);
        _local = new WebLlmEngine(JS);
        _local.LoadProgress += OnLocalProgress;
        _keys = new KazoApiKeyStore(AppContext);
        _downloader = new BrowserFileDownloader(JS);
        _picker = new BrowserFilePicker(JS);
        var store = new AiStore(new KazoAiFileSystem(AppContext), AppContext.HomeDirectory);
        var factory = new ProviderFactory(_http, _keys, _local);
        _session = new ChatSession(store, _keys, factory.Create, ModuleRegistry.CreateDefault());
        _session.Changed += OnSessionChanged;
        try
        {
            await _session.InitializeAsync();
        }
        catch (IOException exception)
        {
            ShowStatus(string.Format(CultureInfo.CurrentCulture, L["LoadFailed"], exception.Message), error: true);
        }

        if (_session.ModuleErrors.Count > 0)
        {
            ShowStatus(string.Format(CultureInfo.CurrentCulture, L["ModuleErrors"], _session.ModuleErrors.Count), error: true);
        }

        _ = PrepareLocalModelAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Kazo.Ai/js/ai-window.js");
            if (_session is not null && !_composerBound)
            {
                _composerBound = true;
                await _module.InvokeVoidAsync("bindComposer", _inputBox);
            }

            if (_session is not null)
            {
                await _module.InvokeVoidAsync("followEnd", _messages);
            }
        }
        catch (JSException)
        {
            // Scrolling and the Enter key are comforts: the window works without them.
        }
    }

    // The local model: what the GPU can do is checked at start; the model is loaded right away
    // when it is already in the browser cache and the settings ask for it.
    private async Task PrepareLocalModelAsync()
    {
        if (_local is null || _session is null || _session.Settings.FindProvider(ProviderSettings.LocalId) is not { Enabled: true })
        {
            return;
        }

        try
        {
            _gpu = await _local.DetectAsync();
            if (_gpu.WebGpu && _session.Settings.AutoLoadLocal && await _local.IsCachedAsync(_session.Settings.LocalModel))
            {
                await _local.LoadAsync(_session.Settings.LocalModel, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is JSException or AiServiceException or InvalidOperationException)
        {
            _localProgress = null;
            _localProgressText = exception.Message;
        }

        await InvokeAsync(StateHasChanged);
    }

    private void OnLocalProgress(double progress, string text)
    {
        _localProgress = progress >= 1 ? null : progress;
        _localProgressText = text;
        QueueRender();
    }

    private void OnSessionChanged() => QueueRender();

    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        TimeSpan wait = RenderInterval - (DateTimeOffset.UtcNow - _lastRender);
        _ = InvokeAsync(async () =>
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }

            _renderQueued = false;
            _lastRender = DateTimeOffset.UtcNow;
            StateHasChanged();
        });
    }

    private async Task SendAsync()
    {
        if (_session is null || Busy || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        string text = _input;
        _input = string.Empty;
        _sidebarOpen = false;
        ClearStatus();
        await _session.SendAsync(text);
        await SetWindowTitleAsync(Current?.Title is { Length: > 0 } title ? FormatWindowTitle(title) : null);
    }

    private async Task OnInputKeyDown(KeyboardEventArgs e)
    {
        // Enter sends, Shift+Enter writes a new line (the browser does it).
        if (e.Key == "Enter" && !e.ShiftKey && !e.IsComposing())
        {
            await SendAsync();
        }
    }

    private void Stop() => _session?.Stop();

    private async Task NewConversationAsync()
    {
        Session.NewConversation();
        _sidebarOpen = false;
        await SetWindowTitleAsync(null);
        await FocusInputAsync();
    }

    private async Task SelectAsync(string id)
    {
        Session.Select(id);
        _sidebarOpen = false;
        await SetWindowTitleAsync(Current?.Title is { Length: > 0 } title ? FormatWindowTitle(title) : null);
    }

    private async Task DeleteConversationAsync(Conversation conversation)
    {
        MessageBoxDialogResult answer = await MessageBox(
            L["DeleteConversation"],
            string.Format(CultureInfo.CurrentCulture, L["DeleteConversationQuestion"], conversation.Title),
            MessageBoxType.YesNo,
            MessageBoxIcon.Warning);
        if (answer.Result == MessageBoxResult.Yes)
        {
            await Session.DeleteConversationAsync(conversation.Id);
        }
    }

    private async Task RenameConversationAsync(Conversation conversation)
    {
        TextInputDialogResult answer = await TextInput(L["RenameConversation"], L["ConversationTitle"], conversation.Title);
        if (answer.Status == TextInputStatus.Submitted && answer.Value is { Length: > 0 } title)
        {
            await Session.RenameConversationAsync(conversation.Id, title);
            if (Current?.Id == conversation.Id)
            {
                await SetWindowTitleAsync(FormatWindowTitle(title));
            }
        }
    }

    private async Task CopyAsync(ConversationMessage message)
    {
        try
        {
            await KazoClipboard.SetTextAsync(message.Content);
            ShowStatus(L["Copied"], error: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            ShowStatus(exception.Message, error: true);
        }
    }

    private Task RateAsync(ConversationMessage message, int rating) =>
        Session.RateAsync(message.Id, message.Rating == rating ? 0 : rating);

    private Task RegenerateAsync() => Session.RegenerateAsync();

    private async Task OnSettingsChangedAsync(AiSettings settings)
    {
        string previousLocal = Session.Settings.LocalModel;
        if (!await TrySaveSettingsAsync(settings))
        {
            return;
        }

        if (previousLocal != settings.LocalModel && _local?.LoadedModel is not null)
        {
            await _local.UnloadAsync();
        }
    }

    private async Task SelectModelAsync(ChangeEventArgs e)
    {
        string value = e.Value?.ToString() ?? string.Empty;
        if (value == RaceOption)
        {
            await TrySaveSettingsAsync(Session.Settings with { Mode = ChatMode.Race });
            return;
        }

        if (ModelRef.TryParse(value, out _))
        {
            await TrySaveSettingsAsync(Session.Settings with { Mode = ChatMode.Single, SelectedModel = value });
        }
    }

    // A settings save that fails (storage full, a conflicting write that retried and failed
    // again) is shown on the status line instead of taking the window down.
    private async Task<bool> TrySaveSettingsAsync(AiSettings settings)
    {
        try
        {
            await Session.UpdateSettingsAsync(settings);
            return true;
        }
        catch (IOException exception)
        {
            ShowStatus(exception.Message, error: true);
            return false;
        }
    }

    private const string RaceOption = "@race";

    // The models offered in the toolbar: the selected one, the typed ones of each enabled
    // service, the race models, and the local model.
    private IReadOnlyList<(string Value, string Label)> ModelChoices()
    {
        AiSettings settings = Session.Settings;
        var values = new List<string>();
        void Add(string? value)
        {
            if (value is not null && ModelRef.TryParse(value, out _) && !values.Contains(value))
            {
                values.Add(value);
            }
        }

        Add(settings.SelectedModel);
        foreach (ProviderSettings provider in settings.Providers.Where(p => p.Enabled))
        {
            if (provider.Kind == ProviderKind.WebLlm)
            {
                Add(ProviderSettings.LocalId + ":" + settings.LocalModel);
                continue;
            }

            foreach (string model in provider.Models ?? [])
            {
                Add(provider.Id + ":" + model);
            }
        }

        foreach (string model in settings.RaceModels)
        {
            Add(model);
        }

        var choices = values.Select(value => (value, ModelLabel(value))).ToList();
        if (settings.RaceModels.Count > 1)
        {
            choices.Add((RaceOption, string.Format(CultureInfo.CurrentCulture, L["RaceOption"], settings.RaceModels.Count)));
        }

        return choices;
    }

    private string SelectedChoice => Session.Settings.Mode == ChatMode.Race && Session.Settings.RaceModels.Count > 1
        ? RaceOption
        : Session.Settings.SelectedModel;

    private string ModelLabel(string? value)
    {
        if (value is null || !ModelRef.TryParse(value, out ModelRef? model) || model is null)
        {
            return value ?? string.Empty;
        }

        string provider = Session.Settings.FindProvider(model.ProviderId)?.Name ?? model.ProviderId;
        return model.ModelId + " · " + provider;
    }

    private string ErrorText(ConversationMessage message)
    {
        string kind = Enum.TryParse(message.ErrorKind, out AiErrorKind parsed) ? parsed.ToString() : nameof(AiErrorKind.Unknown);
        string hint = L["Error" + kind];
        return string.IsNullOrEmpty(message.Error) ? hint : hint + " (" + message.Error + ")";
    }

    private async Task ExportSettingsAsync()
    {
        if (_downloader is null)
        {
            return;
        }

        string json = SettingsExport.Write(Session.Settings, Session.FileModules, DateTimeOffset.UtcNow);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await _downloader.DownloadFileFromStreamAsync("kazo-ai-settings.json", stream);
        ShowStatus(L["Exported"], error: false);
    }

    private async Task ImportSettingsAsync()
    {
        if (_picker is null)
        {
            return;
        }

        try
        {
            BrowserPickedFile? file = await _picker.PickAsync(".json,application/json");
            if (file is null)
            {
                return;
            }

            (AiSettings settings, IReadOnlyList<DeclarativeModule> modules) = SettingsExport.Read(Encoding.UTF8.GetString(file.Content));
            foreach (DeclarativeModule module in modules)
            {
                await Session.AddModuleAsync(module);
            }

            await Session.UpdateSettingsAsync(settings);
            ShowStatus(string.Format(CultureInfo.CurrentCulture, L["Imported"], modules.Count), error: false);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            ShowStatus(string.Format(CultureInfo.CurrentCulture, L["ImportFailed"], exception.Message), error: true);
        }
    }

    private async Task ImportModuleAsync()
    {
        if (_picker is null)
        {
            return;
        }

        try
        {
            BrowserPickedFile? file = await _picker.PickAsync(".json,application/json");
            if (file is null)
            {
                return;
            }

            DeclarativeModule module = DeclarativeModule.Parse(Encoding.UTF8.GetString(file.Content));
            await Session.AddModuleAsync(module);
            ShowStatus(string.Format(CultureInfo.CurrentCulture, L["ModuleImported"], module.Name), error: false);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException)
        {
            ShowStatus(string.Format(CultureInfo.CurrentCulture, L["ImportFailed"], exception.Message), error: true);
        }
    }

    private async Task FocusInputAsync()
    {
        try
        {
            await _inputBox.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // Not rendered yet.
        }
    }

    private void ShowStatus(string text, bool error)
    {
        _status = text;
        _statusIsError = error;
        QueueRender();
    }

    private void ClearStatus() => _status = null;

    private static string Duration(long? milliseconds) => milliseconds is { } ms
        ? (ms < 1000 ? ms.ToString(CultureInfo.CurrentCulture) + " ms" : (ms / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " s")
        : string.Empty;
}

/// <summary>Small helpers for keyboard events.</summary>
internal static class KeyboardEventArgsExtensions
{
    /// <summary>Gets whether an input method is composing (Enter then confirms a word, it does not send).</summary>
    /// <param name="e">The event.</param>
    /// <returns>Whether the key belongs to a composition (the browser reports its code as <c>Process</c>).</returns>
    public static bool IsComposing(this KeyboardEventArgs e) => e.Code == "Process";
}

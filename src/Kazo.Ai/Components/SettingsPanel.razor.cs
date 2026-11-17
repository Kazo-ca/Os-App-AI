using System.Globalization;
using System.Text.RegularExpressions;
using Kazo.Ai.Http;
using Kazo.Ai.Providers;
using Kazo.Ai.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Ai.Components;

/// <summary>The tabs of <see cref="SettingsPanel"/>.</summary>
public enum SettingsTab
{
    /// <summary>The AI services and their keys.</summary>
    Services,

    /// <summary>The local model.</summary>
    Local,

    /// <summary>Mode, race, AutoTune and persona.</summary>
    Chat,

    /// <summary>The modules.</summary>
    Modules,

    /// <summary>Import and export.</summary>
    Data,
}

/// <summary>The settings panel. Every change goes to <see cref="OnChanged"/>, which saves it.</summary>
public sealed partial class SettingsPanel
{
    private readonly Dictionary<string, string> _keyDrafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ModelInfo>> _listed = new(StringComparer.Ordinal);
    private IReadOnlyList<LocalModelInfo> _localModels = [];
    private SettingsTab _tab = SettingsTab.Services;
    private string? _listing;
    private string _newName = string.Empty;
    private string _newUrl = string.Empty;
    private string _personaEmoji = "★";
    private string _personaName = string.Empty;
    private string _personaPrompt = string.Empty;
    private bool _localCached;
    private bool _localBusy;
    private string? _localError;
    private string? _serviceError;

    /// <summary>Gets or sets the chat.</summary>
    [Parameter, EditorRequired]
    public Chat.ChatSession Session { get; set; } = default!;

    /// <summary>Gets or sets the app's texts.</summary>
    [Parameter, EditorRequired]
    public IStringLocalizer L { get; set; } = default!;

    /// <summary>Gets or sets the API keys.</summary>
    [Parameter, EditorRequired]
    public IApiKeyStore Keys { get; set; } = default!;

    /// <summary>Gets or sets the local model engine.</summary>
    [Parameter, EditorRequired]
    public ILocalModelEngine Local { get; set; } = default!;

    /// <summary>Gets or sets what the browser's GPU can do (null until checked).</summary>
    [Parameter]
    public LocalGpuInfo? Gpu { get; set; }

    /// <summary>Gets or sets how far the local model's download got (null when none runs).</summary>
    [Parameter]
    public double? LocalProgress { get; set; }

    /// <summary>Gets or sets the text of the local model's download.</summary>
    [Parameter]
    public string LocalProgressText { get; set; } = string.Empty;

    /// <summary>Gets or sets what saves new settings.</summary>
    [Parameter]
    public EventCallback<AiSettings> OnChanged { get; set; }

    /// <summary>Gets or sets what exports the settings.</summary>
    [Parameter]
    public EventCallback OnExport { get; set; }

    /// <summary>Gets or sets what imports settings.</summary>
    [Parameter]
    public EventCallback OnImport { get; set; }

    /// <summary>Gets or sets what imports a JSON module.</summary>
    [Parameter]
    public EventCallback OnImportModule { get; set; }

    /// <summary>Gets or sets what closes the panel.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    private AiSettings Settings => Session.Settings;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        foreach (ProviderSettings provider in Settings.Providers.Where(p => p.NeedsKey))
        {
            if (!string.IsNullOrEmpty(await Keys.GetAsync(provider.Id)))
            {
                _withKey.Add(provider.Id);
            }
        }

        await RefreshLocalAsync();
    }

    private async Task RefreshLocalAsync()
    {
        try
        {
            _localModels = await Local.ListModelsAsync();
            _localCached = await Local.IsCachedAsync(Settings.LocalModel);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or AiServiceException)
        {
            _localError = exception.Message;
        }
    }

    private Task ApplyAsync(AiSettings settings) => OnChanged.InvokeAsync(settings);

    private Task UpdateProviderAsync(ProviderSettings provider)
    {
        bool known = Settings.Providers.Any(p => p.Id == provider.Id);
        IReadOnlyList<ProviderSettings> providers = known
            ? [.. Settings.Providers.Select(p => p.Id == provider.Id ? provider with { BaseUrl = provider.BaseUrl.Trim() } : p)]
            : [.. Settings.Providers, provider];
        return ApplyAsync(Settings with { Providers = providers });
    }

    private Task RemoveProviderAsync(ProviderSettings provider) =>
        ApplyAsync(Settings with { Providers = [.. Settings.Providers.Where(p => p.Id != provider.Id)] });

    private async Task AddProviderAsync()
    {
        string id = Slug(_newName);
        int suffix = 2;
        string unique = id;
        while (Settings.Providers.Any(p => p.Id == unique) || unique == ProviderSettings.LocalId)
        {
            unique = id + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        await UpdateProviderAsync(new ProviderSettings(unique, _newName.Trim(), ProviderKind.OpenAiCompatible, _newUrl.Trim()));
        _newName = string.Empty;
        _newUrl = string.Empty;
    }

    private Task AddModelAsync(ProviderSettings provider, string model)
    {
        model = model.Trim();
        if (model.Length == 0 || (provider.Models ?? []).Contains(model))
        {
            return Task.CompletedTask;
        }

        return UpdateProviderAsync(provider with { Models = [.. provider.Models ?? [], model] });
    }

    private async Task ListModelsAsync(ProviderSettings provider)
    {
        if (!Session.Providers.TryGetValue(provider.Id, out IChatProvider? service))
        {
            return;
        }

        _listing = provider.Id;
        _serviceError = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _listed[provider.Id] = await service.ListModelsAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is AiServiceException or OperationCanceledException or InvalidOperationException)
        {
            _listed.Remove(provider.Id);
            await ShowErrorAsync(exception.Message);
        }
        finally
        {
            _listing = null;
        }
    }

    private async Task SaveKeyAsync(ProviderSettings provider)
    {
        string key = KeyDraft(provider.Id).Trim();
        if (key.Length == 0)
        {
            return;
        }

        await Keys.SetAsync(provider.Id, provider.Name, key);
        _keyDrafts.Remove(provider.Id);
        _withKey.Add(provider.Id);
    }

    private async Task DeleteKeyAsync(ProviderSettings provider)
    {
        await Keys.DeleteAsync(provider.Id);
        _withKey.Remove(provider.Id);
    }

    private async Task LoadLocalAsync()
    {
        _localBusy = true;
        _localError = null;
        try
        {
            await Local.LoadAsync(Settings.LocalModel, CancellationToken.None);
        }
        catch (Exception exception) when (exception is JSException or AiServiceException or InvalidOperationException)
        {
            _localError = exception.Message;
        }
        finally
        {
            _localBusy = false;
            await RefreshLocalAsync();
        }
    }

    private async Task UnloadLocalAsync()
    {
        _localBusy = true;
        try
        {
            await Local.UnloadAsync();
        }
        finally
        {
            _localBusy = false;
        }
    }

    private async Task DeleteLocalAsync()
    {
        _localBusy = true;
        try
        {
            if (Local.LoadedModel == Settings.LocalModel)
            {
                await Local.UnloadAsync();
            }

            await Local.DeleteAsync(Settings.LocalModel);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            _localError = exception.Message;
        }
        finally
        {
            _localBusy = false;
            await RefreshLocalAsync();
        }
    }

    private Task ToggleModuleAsync(string id, bool enabled) => ApplyAsync(Settings with
    {
        EnabledModules = enabled
            ? [.. Settings.EnabledModules.Where(m => m != id), id]
            : [.. Settings.EnabledModules.Where(m => m != id)],
    });

    private async Task AddPersonaAsync()
    {
        string id = Slug(_personaName);
        if (Settings.AllPersonas.Any(p => p.Id == id))
        {
            id += "_" + Settings.CustomPersonas.Count.ToString(CultureInfo.InvariantCulture);
        }

        var persona = new Persona(id, _personaName.Trim(), string.IsNullOrWhiteSpace(_personaEmoji) ? "★" : _personaEmoji.Trim(), _personaPrompt.Trim());
        await ApplyAsync(Settings with { CustomPersonas = [.. Settings.CustomPersonas, persona] });
        _personaName = string.Empty;
        _personaPrompt = string.Empty;
    }

    private Task RemovePersonaAsync(Persona persona) => ApplyAsync(Settings with
    {
        CustomPersonas = [.. Settings.CustomPersonas.Where(p => p.Id != persona.Id)],
        PersonaId = Settings.PersonaId == persona.Id ? BuiltInPersonas.Assistant.Id : Settings.PersonaId,
    });

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            await OnClose.InvokeAsync();
        }
    }

    private Task ShowErrorAsync(string message)
    {
        _serviceError = message;
        return Task.CompletedTask;
    }

    private bool HasKey(string providerId) => _withKey.Contains(providerId);

    private string KeyDraft(string providerId) => _keyDrafts.GetValueOrDefault(providerId) ?? string.Empty;

    private static bool IsPreset(string id) => ProviderPresets.All.Any(p => p.Id == id);

    private static bool IsChecked(ChangeEventArgs e) => e.Value is bool value ? value : bool.TryParse(e.Value?.ToString(), out bool parsed) && parsed;

    private static string Text(ChangeEventArgs e) => e.Value?.ToString() ?? string.Empty;

    private static double Number(ChangeEventArgs e, double fallback) =>
        double.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;

    private static IReadOnlyList<string> Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>Lower-case letters and digits of <paramref name="name"/>, each other run becoming one <c>_</c>.</summary>
    /// <remarks>
    /// Written without Regex: the trimmed framework of Kazo OS does not keep the static
    /// <c>Regex.Replace</c> with a timeout, and the Store refuses an app that calls it.
    /// </remarks>
    private static string Slug(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(c);
            }
            else if (builder.Length == 0 || builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        string slug = builder.ToString().Trim('_');
        return slug.Length == 0 ? "service" : slug.Length > 32 ? slug[..32] : slug;
    }
}

using System.Text.Json;
using Kazo.Ai.Chat;
using Kazo.Ai.Modules;
using Kazo.Ai.Settings;
using Kazo.Ai.Tuning;

namespace Kazo.Ai.Storage;

/// <summary>
/// Reads and writes the app's files in <c>~/.config/ca.kazo.ai/</c>: <c>settings.json</c>,
/// <c>feedback.json</c>, <c>conversations/*.json</c> and <c>modules/*.json</c>. A missing or broken
/// file never stops the app: it reads as the defaults, and a broken conversation is skipped.
/// </summary>
public sealed class AiStore
{
    /// <summary>The conversations kept in the list (older ones stay on disk).</summary>
    public const int MaxConversationsListed = 300;

    private readonly IAiFileSystem _files;
    private readonly string _home;

    /// <summary>Creates the store.</summary>
    /// <param name="files">The file system.</param>
    /// <param name="home">The user's home.</param>
    public AiStore(IAiFileSystem files, string home)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(home);
        _files = files;
        _home = home;
    }

    /// <summary>Reads the settings.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The settings, normalised, or the defaults.</returns>
    public async Task<AiSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        string? json = await TryReadAsync(AiPaths.Settings(_home), cancellationToken);
        return ParseSettings(json) ?? new AiSettings().Normalize();
    }

    /// <summary>Reads a settings JSON.</summary>
    /// <param name="json">The JSON, or null.</param>
    /// <returns>The settings, normalised, or null when not valid.</returns>
    public static AiSettings? ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AiJsonContext.Default.AiSettings)?.Normalize();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the settings.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public Task SaveSettingsAsync(AiSettings settings, CancellationToken cancellationToken = default) =>
        _files.WriteAllTextAsync(AiPaths.Settings(_home), JsonSerializer.Serialize(settings.Normalize(), AiJsonContext.Default.AiSettings), cancellationToken);

    /// <summary>Reads the ratings.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The ratings, or none.</returns>
    public async Task<FeedbackState> LoadFeedbackAsync(CancellationToken cancellationToken = default)
    {
        string? json = await TryReadAsync(AiPaths.Feedback(_home), cancellationToken);
        if (json is null)
        {
            return FeedbackState.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AiJsonContext.Default.FeedbackState) ?? FeedbackState.Empty;
        }
        catch (JsonException)
        {
            return FeedbackState.Empty;
        }
    }

    /// <summary>Writes the ratings.</summary>
    /// <param name="state">The ratings.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public Task SaveFeedbackAsync(FeedbackState state, CancellationToken cancellationToken = default) =>
        _files.WriteAllTextAsync(AiPaths.Feedback(_home), JsonSerializer.Serialize(state, AiJsonContext.Default.FeedbackState), cancellationToken);

    /// <summary>Reads every conversation, newest first.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The conversations.</returns>
    public async Task<IReadOnlyList<Conversation>> LoadConversationsAsync(CancellationToken cancellationToken = default)
    {
        var conversations = new List<Conversation>();
        foreach (string path in await _files.ListFilesAsync(AiPaths.Conversations(_home), ".json", cancellationToken))
        {
            string? json = await TryReadAsync(path, cancellationToken);
            if (json is null)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize(json, AiJsonContext.Default.Conversation) is { } conversation && IsSafeId(conversation.Id))
                {
                    conversations.Add(conversation);
                }
            }
            catch (JsonException)
            {
                // A broken file is skipped; it stays on disk for the person to look at.
            }
        }

        return [.. conversations.OrderByDescending(c => c.UpdatedAt).Take(MaxConversationsListed)];
    }

    /// <summary>Writes a conversation.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public Task SaveConversationAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return _files.WriteAllTextAsync(ConversationPath(conversation.Id), JsonSerializer.Serialize(conversation, AiJsonContext.Default.Conversation), cancellationToken);
    }

    /// <summary>Deletes a conversation.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public Task DeleteConversationAsync(string id, CancellationToken cancellationToken = default) =>
        _files.DeleteAsync(ConversationPath(id), cancellationToken);

    /// <summary>Reads the JSON modules; a broken file is reported, not loaded.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The modules and, per broken file, why.</returns>
    public async Task<(IReadOnlyList<DeclarativeModule> Modules, IReadOnlyList<string> Errors)> LoadModulesAsync(CancellationToken cancellationToken = default)
    {
        var modules = new List<DeclarativeModule>();
        var errors = new List<string>();
        foreach (string path in await _files.ListFilesAsync(AiPaths.Modules(_home), ".json", cancellationToken))
        {
            string? json = await TryReadAsync(path, cancellationToken);
            if (json is null)
            {
                continue;
            }

            try
            {
                modules.Add(DeclarativeModule.Parse(json));
            }
            catch (FormatException exception)
            {
                errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return (modules, errors);
    }

    /// <summary>Writes a JSON module (imported from the settings).</summary>
    /// <param name="module">The module.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public Task SaveModuleAsync(DeclarativeModule module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        return _files.WriteAllTextAsync(
            AiPaths.Modules(_home) + "/" + module.Id + ".json",
            JsonSerializer.Serialize(module.Definition, AiJsonContext.Default.DeclarativeModuleDefinition),
            cancellationToken);
    }

    /// <summary>Deletes a JSON module.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public Task DeleteModuleAsync(string id, CancellationToken cancellationToken = default) =>
        ModuleRegistry.IsValidId(id) ? _files.DeleteAsync(AiPaths.Modules(_home) + "/" + id + ".json", cancellationToken) : Task.CompletedTask;

    /// <summary>Whether an id can name a file (32 hex digits or a module-like id).</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it is safe.</returns>
    public static bool IsSafeId(string? id) => ModuleRegistry.IsValidId(id);

    private string ConversationPath(string id)
    {
        if (!IsSafeId(id))
        {
            throw new ArgumentException($"'{id}' is not a valid conversation id.", nameof(id));
        }

        return AiPaths.Conversations(_home) + "/" + id + ".json";
    }

    private async Task<string?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _files.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

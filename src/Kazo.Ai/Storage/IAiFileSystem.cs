using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Ai.Storage;

/// <summary>
/// The few file operations the app needs, on the app's own folder <c>~/.config/ca.kazo.ai/</c>.
/// The app uses <see cref="KazoAiFileSystem"/>; the tests use <see cref="MemoryAiFileSystem"/>.
/// </summary>
public interface IAiFileSystem
{
    /// <summary>Reads a file.</summary>
    /// <param name="path">Its absolute path.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="IOException">No such file.</exception>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes a file, creating its folder (under the app's folder) when needed.</summary>
    /// <param name="path">Its absolute path.</param>
    /// <param name="text">Its text.</param>
    /// <param name="cancellationToken">Cancels.</param>
    Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken);

    /// <summary>Lists the files of a folder whose name ends with <paramref name="extension"/>.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="extension">For example <c>.json</c>.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>Their absolute paths, or an empty list when the folder does not exist.</returns>
    Task<IReadOnlyList<string>> ListFilesAsync(string folder, string extension, CancellationToken cancellationToken);

    /// <summary>Deletes a file (nothing when it does not exist).</summary>
    /// <param name="path">Its absolute path.</param>
    /// <param name="cancellationToken">Cancels.</param>
    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

/// <summary><see cref="IAiFileSystem"/> on the Kazo OS file system, inside the app's context.</summary>
public sealed class KazoAiFileSystem : IAiFileSystem
{
    /// <summary>Mode of the app's folders (<c>0700</c>: conversations are private).</summary>
    private static readonly FileSystemPermissions FolderMode = FileSystemPermissions.FromMode(0b111_000_000);

    private readonly IAppExecutionContext _context;
    private readonly HashSet<string> _knownFolders = new(StringComparer.Ordinal);

    /// <summary>Creates the file system of the app.</summary>
    /// <param name="context">The app's context, entered for every call (calls come from child components and background tasks).</param>
    public KazoAiFileSystem(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        using IDisposable scope = KazoExecutionContext.Enter(_context);
        return await KazoFile.ReadAllTextAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        using IDisposable scope = KazoExecutionContext.Enter(_context);
        if (KazoPath.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            await EnsureFolderAsync(folder, cancellationToken);
        }

        await KazoFile.WriteAllTextAsync(path, text, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListFilesAsync(string folder, string extension, CancellationToken cancellationToken)
    {
        using IDisposable scope = KazoExecutionContext.Enter(_context);
        if (!await KazoDirectory.ExistsAsync(folder, cancellationToken))
        {
            return [];
        }

        string[] files = await KazoDirectory.GetFilesAsync(folder, "*", SearchOption.TopDirectoryOnly, cancellationToken);
        return [.. files.Where(file => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using IDisposable scope = KazoExecutionContext.Enter(_context);
        if (await KazoFile.ExistsAsync(path, cancellationToken))
        {
            await KazoFile.DeleteAsync(path, cancellationToken);
        }
    }

    /// <summary>
    /// Creates the app's folders one level at a time, the way <c>mkdir</c> without <c>-p</c> does:
    /// with only <c>filesystem.private.*</c>, looking at <c>~/.config</c> itself is not allowed, so
    /// <c>KazoDirectory.CreateDirectoryAsync</c> (which checks every parent) cannot be used.
    /// </summary>
    private async Task EnsureFolderAsync(string folder, CancellationToken cancellationToken)
    {
        if (_knownFolders.Contains(folder))
        {
            return;
        }

        string root = AiPaths.AppFolder(_context.HomeDirectory);
        var chain = new List<string>();
        for (string? current = folder; current is { Length: > 0 } && current.StartsWith(root, StringComparison.Ordinal); current = KazoPath.GetDirectoryName(current))
        {
            chain.Insert(0, current);
            if (current == root)
            {
                break;
            }
        }

        foreach (string level in chain)
        {
            if (_knownFolders.Contains(level))
            {
                continue;
            }

            FileSystemMutationResult created = await _context.FileSystem.CreateAsync(
                new FileSystemCreateRequest(VirtualPath.Parse(level), FileSystemEntryKind.Directory, FolderMode),
                cancellationToken);
            if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
            {
                FileSystemError? error = created.Transaction.Error;
                throw error?.Code is FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied or FileSystemErrorCode.AuthorityDenied
                    ? new UnauthorizedAccessException($"Access to the path '{level}' is denied ({error.Code}).")
                    : new IOException($"The folder '{level}' could not be created ({error?.Code}).");
            }

            _knownFolders.Add(level);
        }
    }
}

/// <summary>Files in memory, for the tests.</summary>
public sealed class MemoryAiFileSystem : IAiFileSystem
{
    /// <summary>Gets the files (path → text).</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        Files.TryGetValue(path, out string? text) ? Task.FromResult(text) : Task.FromException<string>(new FileNotFoundException(path));

    /// <inheritdoc />
    public Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        Files[path] = text;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListFilesAsync(string folder, string extension, CancellationToken cancellationToken)
    {
        string prefix = folder.TrimEnd('/') + "/";
        IReadOnlyList<string> files = [.. Files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && !path[prefix.Length..].Contains('/') && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
        return Task.FromResult(files);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        Files.Remove(path);
        return Task.CompletedTask;
    }
}

/// <summary>The app's file paths.</summary>
public static class AiPaths
{
    /// <summary>The app's folder: <c>&lt;home&gt;/.config/ca.kazo.ai</c>.</summary>
    /// <param name="home">The user's home.</param>
    /// <returns>The folder.</returns>
    public static string AppFolder(string home) => home.TrimEnd('/') + "/.config/" + AiApp.AppId;

    /// <summary>The settings file.</summary>
    /// <param name="home">The user's home.</param>
    /// <returns><c>settings.json</c> in the app's folder.</returns>
    public static string Settings(string home) => AppFolder(home) + "/settings.json";

    /// <summary>The ratings file.</summary>
    /// <param name="home">The user's home.</param>
    /// <returns><c>feedback.json</c> in the app's folder.</returns>
    public static string Feedback(string home) => AppFolder(home) + "/feedback.json";

    /// <summary>The folder of the conversations.</summary>
    /// <param name="home">The user's home.</param>
    /// <returns><c>conversations/</c> in the app's folder.</returns>
    public static string Conversations(string home) => AppFolder(home) + "/conversations";

    /// <summary>The folder of the JSON modules.</summary>
    /// <param name="home">The user's home.</param>
    /// <returns><c>modules/</c> in the app's folder.</returns>
    public static string Modules(string home) => AppFolder(home) + "/modules";
}

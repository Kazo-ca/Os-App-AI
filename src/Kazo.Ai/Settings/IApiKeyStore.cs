using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Keyring;

namespace Kazo.Ai.Settings;

/// <summary>
/// Where the API keys are kept: the OS's encrypted keyring, under <c>provider/&lt;id&gt;</c>, never a
/// file, never an export. The tests use a dictionary.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>Reads a provider's key.</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The key, or null.</returns>
    ValueTask<string?> GetAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Keeps (or replaces) a provider's key.</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="providerName">Its name, for the keyring's label.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels.</param>
    ValueTask SetAsync(string providerId, string providerName, string key, CancellationToken cancellationToken = default);

    /// <summary>Forgets a provider's key.</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="cancellationToken">Cancels.</param>
    ValueTask DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>The keys in the app's part of the OS keyring (<c>KazoKeyring</c>, no capability needed).</summary>
public sealed class KazoApiKeyStore : IApiKeyStore
{
    private readonly IAppExecutionContext _context;

    /// <summary>Creates the store.</summary>
    /// <param name="context">The app's context.</param>
    public KazoApiKeyStore(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>The keyring entry of a provider's key.</summary>
    /// <param name="providerId">The provider.</param>
    /// <returns><c>provider/&lt;id&gt;</c>.</returns>
    public static string EntryId(string providerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        return "provider/" + providerId;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        using IDisposable scope = KazoExecutionContext.Enter(_context);
        return await KazoKeyring.GetAsync(_context, EntryId(providerId), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string providerId, string providerName, string key, CancellationToken cancellationToken = default)
    {
        using IDisposable scope = KazoExecutionContext.Enter(_context);
        await KazoKeyring.SetAsync(_context, EntryId(providerId), key, $"Kazo AI: {providerName}", cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        using IDisposable scope = KazoExecutionContext.Enter(_context);
        await KazoKeyring.DeleteAsync(_context, EntryId(providerId), cancellationToken);
    }
}

/// <summary>Keys in memory, for the tests.</summary>
public sealed class MemoryApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string providerId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_keys.GetValueOrDefault(providerId));

    /// <inheritdoc />
    public ValueTask SetAsync(string providerId, string providerName, string key, CancellationToken cancellationToken = default)
    {
        _keys[providerId] = key;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        _keys.Remove(providerId);
        return ValueTask.CompletedTask;
    }
}

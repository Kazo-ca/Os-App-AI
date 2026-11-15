using Kazo.Ai.Http;
using Kazo.Ai.Settings;

namespace Kazo.Ai.Providers;

/// <summary>
/// Makes the providers of the settings. To add a kind of provider (a service that does not speak
/// the OpenAI format), add a <see cref="ProviderKind"/> and a case here (doc/en/providers.md).
/// </summary>
public sealed class ProviderFactory
{
    private readonly IStreamingHttp _http;
    private readonly IApiKeyStore _keys;
    private readonly ILocalModelEngine _local;

    /// <summary>Creates the factory.</summary>
    /// <param name="http">The transport of the remote services.</param>
    /// <param name="keys">The API keys.</param>
    /// <param name="local">The local engine.</param>
    public ProviderFactory(IStreamingHttp http, IApiKeyStore keys, ILocalModelEngine local)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(local);
        _http = http;
        _keys = keys;
        _local = local;
    }

    /// <summary>Makes the enabled providers of the settings.</summary>
    /// <param name="settings">The settings (read again by the local provider for its model).</param>
    /// <returns>The providers, by id.</returns>
    public IReadOnlyDictionary<string, IChatProvider> Create(Func<AiSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var providers = new Dictionary<string, IChatProvider>(StringComparer.Ordinal);
        foreach (ProviderSettings provider in settings().Providers.Where(p => p.Enabled))
        {
            providers[provider.Id] = provider.Kind switch
            {
                ProviderKind.WebLlm => new WebLlmProvider(_local, () => settings().LocalModel),
                _ => new OpenAiCompatibleProvider(provider, _http, cancellationToken => _keys.GetAsync(provider.Id, cancellationToken)),
            };
        }

        return providers;
    }
}

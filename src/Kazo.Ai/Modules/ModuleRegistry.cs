using Kazo.Ai.Chat;
using Kazo.Ai.Race;

namespace Kazo.Ai.Modules;

/// <summary>
/// The modules the app knows (built-in, C# extensions, JSON files) and which ones run. The enabled
/// list comes from the settings (<see cref="Settings.AiSettings.EnabledModules"/>).
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IChatModule> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);

    /// <summary>Creates a registry with the built-in modules and the C# extensions.</summary>
    /// <returns>The registry.</returns>
    public static ModuleRegistry CreateDefault()
    {
        var registry = new ModuleRegistry();
        foreach (IChatModule module in BuiltInModules.All)
        {
            registry.Register(module, "built-in");
        }

        Extensions.ExtensionModules.RegisterAll(registry);
        return registry;
    }

    /// <summary>Gets or sets the scorer of the races (an extension may replace it).</summary>
    public IResponseScorer Scorer { get; set; } = HeuristicResponseScorer.Instance;

    /// <summary>Gets every module, in order.</summary>
    public IReadOnlyList<IChatModule> Modules => [.. _modules.Values.OrderBy(module => module.Order).ThenBy(module => module.Id, StringComparer.Ordinal)];

    /// <summary>Whether an id is valid: 1 to 64 lower-case letters, digits, '_' or '-'.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>Adds or replaces a module.</summary>
    /// <param name="module">The module.</param>
    /// <param name="origin">Where it comes from (<c>built-in</c>, <c>extension</c>, <c>file</c>).</param>
    /// <exception cref="ArgumentException">Its id is not valid.</exception>
    public void Register(IChatModule module, string origin = "extension")
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!IsValidId(module.Id))
        {
            throw new ArgumentException($"The module id '{module.Id}' is not valid.", nameof(module));
        }

        _modules[module.Id] = module;
        _origins[module.Id] = origin;
    }

    /// <summary>Removes the modules of one origin (before reading the module files again).</summary>
    /// <param name="origin">The origin.</param>
    public void RemoveOrigin(string origin)
    {
        foreach (string id in _origins.Where(pair => pair.Value == origin).Select(pair => pair.Key).ToList())
        {
            _modules.Remove(id);
            _origins.Remove(id);
        }
    }

    /// <summary>Where a module comes from.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Its origin, or null.</returns>
    public string? OriginOf(string id) => _origins.GetValueOrDefault(id);

    /// <summary>Finds a module.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The module, or null.</returns>
    public IChatModule? Find(string id) => _modules.GetValueOrDefault(id);

    /// <summary>Runs the request part of the enabled modules.</summary>
    /// <param name="request">The request.</param>
    /// <param name="enabled">The ids of the enabled modules.</param>
    /// <param name="context">The context (its <see cref="ModuleContext.Config"/> is replaced by each module's).</param>
    /// <param name="configs">Each module's settings.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The request to send and the modules that ran.</returns>
    public async Task<(ChatRequest Request, IReadOnlyList<string> Applied)> TransformRequestAsync(
        ChatRequest request,
        IReadOnlyCollection<string> enabled,
        ModuleContext context,
        IReadOnlyDictionary<string, Dictionary<string, string>>? configs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(context);
        var applied = new List<string>();
        foreach (IChatModule module in Active(enabled, ModuleStage.Request))
        {
            IReadOnlyDictionary<string, string> config = configs?.GetValueOrDefault(module.Id) ?? new Dictionary<string, string>();
            request = await module.TransformRequestAsync(request, context with { Config = config }, cancellationToken);
            applied.Add(module.Id);
        }

        return (request, applied);
    }

    /// <summary>Runs the answer part of the enabled modules.</summary>
    /// <param name="response">The answer.</param>
    /// <param name="enabled">The ids of the enabled modules.</param>
    /// <param name="configs">Each module's settings.</param>
    /// <returns>The answer to show and the modules that ran.</returns>
    public (string Response, IReadOnlyList<string> Applied) TransformResponse(
        string response,
        IReadOnlyCollection<string> enabled,
        IReadOnlyDictionary<string, Dictionary<string, string>>? configs)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(enabled);
        var applied = new List<string>();
        foreach (IChatModule module in Active(enabled, ModuleStage.Response))
        {
            response = module.TransformResponse(response, configs?.GetValueOrDefault(module.Id));
            applied.Add(module.Id);
        }

        return (response, applied);
    }

    private IEnumerable<IChatModule> Active(IReadOnlyCollection<string> enabled, ModuleStage stage) =>
        Modules.Where(module => enabled.Contains(module.Id) && module.Stages.HasFlag(stage));
}

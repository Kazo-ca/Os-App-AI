using Kazo.Ai.Modules;

namespace Kazo.Ai.Extensions;

/// <summary>
/// The place to add C# modules to the app, at build time. An installed Kazo OS app is one assembly
/// (Kazo-ca/Os#419), so extensions are compiled in: put a class implementing
/// <see cref="IChatModule"/> in this folder and register it in <see cref="RegisterExtensions"/>,
/// in a new file of this folder (doc/en/extending.md, "C# extensions").
/// </summary>
/// <example>
/// <code>
/// // src/Kazo.Ai/Extensions/MyExtensions.cs
/// namespace Kazo.Ai.Extensions;
///
/// public static partial class ExtensionModules
/// {
///     static partial void RegisterExtensions(ModuleRegistry registry)
///     {
///         registry.Register(new MyModule());
///         // registry.Scorer = new MyScorer();
///     }
/// }
/// </code>
/// </example>
public static partial class ExtensionModules
{
    /// <summary>Registers the extensions; called once when the window opens.</summary>
    /// <param name="registry">The registry.</param>
    public static void RegisterAll(ModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        RegisterExtensions(registry);
    }

    /// <summary>Implemented in another file of this folder to add modules; does nothing when absent.</summary>
    /// <param name="registry">The registry.</param>
    static partial void RegisterExtensions(ModuleRegistry registry);
}

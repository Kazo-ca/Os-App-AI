using Kazo.Os.App.Abstractions;

namespace Kazo.Ai;

/// <summary>
/// The app's manifest. The build generates <c>manifest.json</c> from this class
/// (Kazo.Os.App.Build); it is never instantiated at run time.
/// </summary>
[AppManifest]
public sealed class AiApp : AppDefinition
{
    /// <summary>The app id, also the name of its folder in <c>~/.config/</c>.</summary>
    public const string AppId = "ca.kazo.ai";

    /// <summary>Width under which the conversation list becomes a side panel (phones, a narrow window).</summary>
    public const int CompactWidth = 760;

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => "Kazo AI";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(AiWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(AiWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazo";

    /// <inheritdoc />
    public override string Description => "Chat with AI models: your own API keys or a local model in the browser";

    /// <inheritdoc />
    public override string Category => "productivity";

    /// <inheritdoc />
    /// <remarks>The tag that publishes to the Kazo OS Store must be v + this version.</remarks>
    public override string Version => "0.1.3";

    /// <inheritdoc />
    /// <remarks>One window per user holds every conversation; launching the app again brings it back.</remarks>
    public override bool SingleInstancePerUser => true;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
    [
        // ~/.config/ca.kazo.ai/: settings.json, feedback.json, conversations/ and modules/.
        AppCapabilities.FileSystemPrivateRead,
        AppCapabilities.FileSystemPrivateWrite,

        // The AI services (OpenRouter, OpenAI, Ollama, LM Studio...) are called from the browser
        // with fetch (wwwroot/js/ai-fetch.js), and the local model downloads its weights from
        // Hugging Face. Dangerous: an administrator approves it. Without it, only a local model
        // already in the browser cache can answer.
        AppCapabilities.NetworkRealAccess,

        // The copy button of an answer.
        AppCapabilities.ClipboardWrite,
    ];

    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];

    /// <inheritdoc />
    public override WindowOptions? Window => new()
    {
        DefaultWidth = 1040,
        DefaultHeight = 680,
        MinWidth = 340,
        MinHeight = 420,
    };
}

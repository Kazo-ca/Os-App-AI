using Kazo.Ai.Chat;

namespace Kazo.Ai.Modules;

/// <summary>Where a module acts.</summary>
[Flags]
public enum ModuleStage
{
    /// <summary>Nowhere (a module that only adds a scorer, for example).</summary>
    None = 0,

    /// <summary>On the request, before it leaves (system text, messages, parameters).</summary>
    Request = 1,

    /// <summary>On the answer, once it is written (a text transformation).</summary>
    Response = 2,
}

/// <summary>
/// An extension of the chat. A module can change the request before it is sent
/// (<see cref="TransformRequestAsync"/>) and the answer once it arrives (<see cref="TransformResponse"/>).
/// The person turns modules on and off in the settings; the enabled ones run in the order of
/// <see cref="Order"/>. See doc/en/modules.md for the three ways to add one (a JSON file, a C#
/// class in <c>src/Kazo.Ai/Extensions/</c>, or a whole new provider).
/// </summary>
/// <remarks>
/// The descriptive members (<see cref="Id"/>, <see cref="Name"/>, <see cref="Description"/>,
/// <see cref="Version"/>, <see cref="Author"/>) are the same as those of common "semantic
/// transformation module" lists of chat tools, and <see cref="TransformResponse"/> has the shape
/// <c>(input, config) =&gt; output</c>, so such a module ports by copying its function body.
/// </remarks>
public interface IChatModule
{
    /// <summary>Gets the id, unique, in <c>snake_case</c> (saved in the settings).</summary>
    string Id { get; }

    /// <summary>Gets the name shown.</summary>
    string Name { get; }

    /// <summary>Gets one sentence shown under the name.</summary>
    string Description { get; }

    /// <summary>Gets the version.</summary>
    string Version { get; }

    /// <summary>Gets the author.</summary>
    string Author { get; }

    /// <summary>Gets where it acts.</summary>
    ModuleStage Stages { get; }

    /// <summary>Gets its place among the modules (smaller first; built-ins use 100, 200...).</summary>
    int Order => 500;

    /// <summary>Changes a request before it is sent.</summary>
    /// <param name="request">The request.</param>
    /// <param name="context">What the module may know about the conversation.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The request to send (the same one to change nothing).</returns>
    ValueTask<ChatRequest> TransformRequestAsync(ChatRequest request, ModuleContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);

    /// <summary>Changes an answer once it is complete.</summary>
    /// <param name="input">The answer.</param>
    /// <param name="config">The module's settings, from the settings file.</param>
    /// <returns>The answer to show.</returns>
    string TransformResponse(string input, IReadOnlyDictionary<string, string>? config) => input;
}

/// <summary>What a module may know when it changes a request.</summary>
/// <param name="ConversationId">The conversation's id.</param>
/// <param name="PersonaId">The persona's id.</param>
/// <param name="ProviderId">The provider's id.</param>
/// <param name="Config">The module's settings, from the settings file.</param>
public sealed record ModuleContext(
    string ConversationId,
    string PersonaId,
    string ProviderId,
    IReadOnlyDictionary<string, string> Config);

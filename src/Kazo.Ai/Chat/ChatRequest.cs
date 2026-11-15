namespace Kazo.Ai.Chat;

/// <summary>
/// What is sent to a model for one answer. The modules (<see cref="Modules.IChatModule"/>) may
/// change it before it leaves, which is why it is a record: each module returns a new one.
/// </summary>
/// <param name="Model">The model id at the provider (for example <c>openai/gpt-4o-mini</c>).</param>
/// <param name="Messages">The messages, the system message first when there is one.</param>
/// <param name="Parameters">The sampling parameters.</param>
/// <param name="MaxTokens">The longest answer, in tokens (null: the provider's default).</param>
public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    SamplingParameters Parameters,
    int? MaxTokens = null)
{
    /// <summary>The text of the last message of the person, or an empty string.</summary>
    public string LastUserText =>
        Messages.LastOrDefault(message => message.Role == ChatRole.User)?.Content ?? string.Empty;

    /// <summary>Returns a copy whose system message is <paramref name="text"/> (added first when missing).</summary>
    /// <param name="text">The new system text.</param>
    /// <returns>The new request.</returns>
    public ChatRequest WithSystem(string text)
    {
        var messages = Messages.Where(message => message.Role != ChatRole.System).ToList();
        if (!string.IsNullOrWhiteSpace(text))
        {
            messages.Insert(0, ChatMessage.System(text));
        }

        return this with { Messages = messages };
    }

    /// <summary>The current system text, or an empty string.</summary>
    public string SystemText => Messages.FirstOrDefault(message => message.Role == ChatRole.System)?.Content ?? string.Empty;
}

/// <summary>A piece of an answer as it streams.</summary>
/// <param name="Text">Text to add to the answer (may be empty).</param>
/// <param name="FinishReason">Why the model stopped, on the last piece (<c>stop</c>, <c>length</c>...).</param>
/// <param name="Usage">Token counts, when the provider sends them.</param>
public sealed record ChatDelta(string Text, string? FinishReason = null, TokenUsage? Usage = null);

/// <summary>Token counts of an answer.</summary>
/// <param name="PromptTokens">Tokens read.</param>
/// <param name="CompletionTokens">Tokens written.</param>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens);

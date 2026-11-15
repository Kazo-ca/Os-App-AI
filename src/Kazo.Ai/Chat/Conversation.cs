using System.Text.Json.Serialization;
using Kazo.Ai.Race;
using Kazo.Ai.Tuning;

namespace Kazo.Ai.Chat;

/// <summary>One message of a saved conversation, with what produced it.</summary>
public sealed record ConversationMessage
{
    /// <summary>Gets the id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets who wrote it.</summary>
    [JsonPropertyName("role")]
    public ChatRole Role { get; init; }

    /// <summary>Gets the text (after the answer modules).</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets when it was written.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the model that wrote it, as <c>provider:model</c>.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Gets how long it took, in milliseconds.</summary>
    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; init; }

    /// <summary>Gets the parameters decision of the request.</summary>
    [JsonPropertyName("autotune")]
    public AutoTuneResult? AutoTune { get; init; }

    /// <summary>Gets the race, when the answer came from one.</summary>
    [JsonPropertyName("race")]
    public RaceSummary? Race { get; init; }

    /// <summary>Gets the modules applied.</summary>
    [JsonPropertyName("modulesApplied")]
    public IReadOnlyList<string> ModulesApplied { get; init; } = [];

    /// <summary>Gets the rating: 1 (👍), -1 (👎) or null.</summary>
    [JsonPropertyName("rating")]
    public int? Rating { get; init; }

    /// <summary>Gets the error, when the answer failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Gets whether the answer was stopped by the person.</summary>
    [JsonPropertyName("stopped")]
    public bool Stopped { get; init; }
}

/// <summary>A conversation, saved as <c>conversations/&lt;id&gt;.json</c>.</summary>
public sealed record Conversation
{
    /// <summary>Gets the id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the persona.</summary>
    [JsonPropertyName("personaId")]
    public string PersonaId { get; init; } = "assistant";

    /// <summary>Gets when it was created.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when it last changed.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the messages.</summary>
    [JsonPropertyName("messages")]
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = [];

    /// <summary>The longest title made from the first message.</summary>
    public const int TitleLength = 48;

    /// <summary>A title from the first message of the person.</summary>
    /// <param name="text">The message.</param>
    /// <returns>Its first line, cut at <see cref="TitleLength"/> characters.</returns>
    public static string TitleFrom(string text)
    {
        string line = (text ?? string.Empty).Trim().Split('\n')[0].Trim();
        if (line.Length <= TitleLength)
        {
            return line;
        }

        int cut = line.LastIndexOf(' ', TitleLength);
        return (cut > TitleLength / 2 ? line[..cut] : line[..TitleLength]).TrimEnd() + "…";
    }

    /// <summary>The messages to send to a model: the system text, then the person's and the model's turns (failed ones left out).</summary>
    /// <param name="systemPrompt">The persona's system text.</param>
    /// <returns>The messages.</returns>
    public IReadOnlyList<ChatMessage> ToChatMessages(string systemPrompt)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(ChatMessage.System(systemPrompt));
        }

        foreach (ConversationMessage message in Messages)
        {
            if (message.Role == ChatRole.System || message.Error is not null || message.Content.Length == 0)
            {
                continue;
            }

            messages.Add(new ChatMessage(message.Role, message.Content));
        }

        return messages;
    }
}

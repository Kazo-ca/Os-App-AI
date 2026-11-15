using System.Text.Json.Serialization;

namespace Kazo.Ai.Chat;

/// <summary>Who wrote a message of a conversation, with the names of the OpenAI chat format.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatRole>))]
public enum ChatRole
{
    /// <summary>Instructions given to the model before the conversation (<c>system</c>).</summary>
    [JsonStringEnumMemberName("system")]
    System,

    /// <summary>The person (<c>user</c>).</summary>
    [JsonStringEnumMemberName("user")]
    User,

    /// <summary>The model (<c>assistant</c>).</summary>
    [JsonStringEnumMemberName("assistant")]
    Assistant,
}

/// <summary>
/// One message sent to a model: the <c>{ "role", "content" }</c> object of the OpenAI chat
/// completions format, which OpenRouter, Ollama, LM Studio and WebLLM all accept.
/// </summary>
/// <param name="Role">Who wrote it.</param>
/// <param name="Content">Its text.</param>
public sealed record ChatMessage(
    [property: JsonPropertyName("role")] ChatRole Role,
    [property: JsonPropertyName("content")] string Content)
{
    /// <summary>Creates a system message.</summary>
    /// <param name="content">The instructions.</param>
    /// <returns>The message.</returns>
    public static ChatMessage System(string content) => new(ChatRole.System, content);

    /// <summary>Creates a message of the person.</summary>
    /// <param name="content">The text.</param>
    /// <returns>The message.</returns>
    public static ChatMessage User(string content) => new(ChatRole.User, content);

    /// <summary>Creates a message of the model.</summary>
    /// <param name="content">The text.</param>
    /// <returns>The message.</returns>
    public static ChatMessage Assistant(string content) => new(ChatRole.Assistant, content);

    /// <summary>The name of a role in the OpenAI format.</summary>
    /// <param name="role">The role.</param>
    /// <returns><c>system</c>, <c>user</c> or <c>assistant</c>.</returns>
    public static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };
}

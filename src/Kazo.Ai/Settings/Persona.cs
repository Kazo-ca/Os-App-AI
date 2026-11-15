using System.Text.Json.Serialization;

namespace Kazo.Ai.Settings;

/// <summary>A persona: the system text that sets how the model behaves, chosen per conversation.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Emoji">A symbol shown next to the name.</param>
/// <param name="SystemPrompt">The system text.</param>
public sealed record Persona(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("systemPrompt")] string SystemPrompt);

/// <summary>The personas shipped with the app; the person adds their own in the settings.</summary>
public static class BuiltInPersonas
{
    /// <summary>The default persona.</summary>
    public static Persona Assistant { get; } = new("assistant", "Assistant", "✦",
        "You are a helpful, knowledgeable assistant. Answer clearly and accurately, and use Markdown when it helps.");

    /// <summary>Every built-in persona.</summary>
    public static IReadOnlyList<Persona> All { get; } =
    [
        Assistant,
        new("developer", "Developer", "⌘",
            "You are a senior software engineer. Give working code with short explanations, point out edge cases, and prefer simple solutions."),
        new("writer", "Writer", "✎",
            "You are a skilled writer and editor. Help with style, structure and tone, and keep the author's voice."),
        new("teacher", "Teacher", "◎",
            "You are a patient teacher. Explain step by step, check understanding with a short question at the end, and use examples."),
        new("none", "No persona", "∅", string.Empty),
    ];
}

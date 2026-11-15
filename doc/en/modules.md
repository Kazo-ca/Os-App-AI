> [Version française](../fr/modules.md)

# Kazo AI modules

A module changes the chat at two moments: the **request**, before it leaves (system message,
sampling parameters, messages), and the **answer**, before it is shown (and, in a race, before it
is scored). The person turns modules on and off in Settings > Modules; the enabled ones run in the
order of their `order` (smaller first).

There are three kinds:

| Kind | Where | Can do |
|---|---|---|
| Built-in | `src/Kazo.Ai/Modules/BuiltInModules.cs` | `concise`, `cite_uncertainty`, `same_language` (instructions added to the system message), `preamble_trimmer`, `whitespace_tidier` (clean the answer). |
| JSON | `~/.config/ca.kazo.ai/modules/<id>.json`, imported from Settings | Add text to the system message, set sampling parameters, replace text in answers with regular expressions. No code. |
| C# | `src/Kazo.Ai/Extensions/` | Anything: a class that implements `IChatModule`, compiled into the app. |

An id is unique: a JSON module whose id is already taken by a built-in or C# module is refused, with
the reason shown in Settings > Modules.

## JSON modules

```json
{
  "id": "formal_french",
  "name": "Formal French",
  "description": "Answers in formal French and spells out units.",
  "version": "1.0.0",
  "author": "you",
  "order": 300,
  "systemPrompt": "Answer in formal French (vouvoiement).",
  "systemPromptPosition": "append",
  "params": { "temperature": 0.5, "top_p": 0.9 },
  "replacements": [
    { "pattern": "\\bkm/h\\b", "replacement": "kilomètres par heure", "flags": "i" }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Lower-case letters, digits, `_` or `-` (64 at most). Also the file name. |
| `name`, `description`, `version`, `author` | no | Shown in Settings. `version` defaults to `1.0.0`. |
| `order` | no | Run order, default 500. |
| `systemPrompt` | no | Text for the system message. |
| `systemPromptPosition` | no | `append` (default), `prepend` or `replace` the persona's prompt. |
| `params` | no | Sampling parameters to force: `temperature`, `top_p`, `top_k`, `frequency_penalty`, `presence_penalty`, `repetition_penalty`. They win over AutoTune. |
| `replacements` | no | Applied to the answer in order. `pattern` is a .NET regular expression (250 ms at most per answer), `replacement` may use `$1`; `flags`: `i` ignore case, `m` multi-line, `s` dot matches new lines. |

A file that is not valid is refused with its reason (`Settings > Modules`). JSON modules are part of
the settings export.

## C# modules

An installed Kazo OS app is one assembly (Kazo-ca/Os#419), so C# modules are compiled into
`Kazo.Ai.dll`. Add a file to `src/Kazo.Ai/Extensions/`:

```csharp
// src/Kazo.Ai/Extensions/MyExtensions.cs
using Kazo.Ai.Chat;
using Kazo.Ai.Modules;

namespace Kazo.Ai.Extensions;

public static partial class ExtensionModules
{
    static partial void RegisterExtensions(ModuleRegistry registry)
    {
        registry.Register(new SignatureModule());
        // registry.Scorer = new MyScorer();   // replaces the race scorer (IResponseScorer)
    }
}

internal sealed class SignatureModule : IChatModule
{
    public string Id => "signature";
    public string Name => "Signature";
    public string Description => "Adds a signature to every answer.";
    public string Version => "1.0.0";
    public string Author => "you";
    public ModuleStage Stages => ModuleStage.Request | ModuleStage.Response;
    public int Order => 900;

    public ValueTask<ChatRequest> TransformRequestAsync(ChatRequest request, ModuleContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.WithSystem(request.SystemText + "\n\nKeep answers under 200 words."));

    public string TransformResponse(string input, IReadOnlyDictionary<string, string>? config) =>
        input + "\n\n— " + (config?.GetValueOrDefault("name") ?? "Kazo AI");
}
```

- `ChatRequest` is a record: return a changed copy (`with`, `WithSystem`).
- `ModuleContext` gives the conversation id, the persona id, the service id (`race` in race mode)
  and the module's own settings (`moduleConfigs` in `settings.json`, a string dictionary).
- `TransformResponse(input, config) => output` has the shape of the "semantic transformation
  modules" of many chat tools: such a module ports by copying its function body to C#.
- `RegisterExtensions` runs once when the window opens. Throwing from a module fails the answer
  with its message; keep modules fast (they run on every answer, for every model of a race).
- Tests: `tests/Kazo.Ai.Tests` shows how to call a module and the registry without a window.

## A new kind of service

Services that speak the OpenAI chat API need no code (Settings > Services > Add). For another API,
implement `IChatProvider` (`ListModelsAsync`, `StreamAsync` returning `ChatDelta` pieces), add a
`ProviderKind` and its case in `ProviderFactory.Create`, and raise `AiServiceException` with the
right `AiErrorKind` so the window can explain failures. See [providers.md](providers.md).

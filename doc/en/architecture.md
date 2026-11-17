> [Version française](../fr/architecture.md)

# Kazo AI architecture

Kazo AI is a Kazo OS window app (`ca.kazo.ai`), one assembly `Kazo.Ai.dll`, written in C# and
Blazor. JavaScript is limited to what C# cannot reach in the browser: `fetch` with a streamed body,
WebGPU (the local model), scrolling and the Enter key of the input box.

## Folders of `src/Kazo.Ai`

| Folder | Role |
|---|---|
| `Chat/` | The data of a chat (`ChatMessage`, `ChatRequest`, `Conversation`) and `ChatSession`, the chat without its window. |
| `Http/` | `IStreamingHttp`, the server-sent events parser (`SseParser`), the errors (`AiServiceException`, `AiErrorKind`) and `BrowserStreamingHttp`, the fetch bridge. |
| `Providers/` | `IChatProvider`, the OpenAI-compatible provider, the local WebGPU provider (`WebLlmProvider`, `WebLlmEngine`), the service settings and presets, `ProviderFactory`. |
| `Race/` | `ModelRace` (several models in parallel), the scorer (`IResponseScorer`, `HeuristicResponseScorer`) and the race results. |
| `Tuning/` | AutoTune: `ContextDetector`, `AutoTuner`, `FeedbackLearner` (learning from 👍 and 👎). |
| `Modules/` | `IChatModule`, `ModuleRegistry`, the built-in modules and the JSON modules (`DeclarativeModule`). |
| `Extensions/` | The place for C# modules compiled into the app ([modules.md](modules.md)). |
| `Settings/` | `AiSettings`, personas, the API key store (OS keyring), the settings export. |
| `Storage/` | `IAiFileSystem` (the app's private folder) and `AiStore` (settings, conversations, ratings, modules). |
| `Markdown/` | `MarkdownRenderer`: the answers' Markdown to safe HTML. |
| `Components/` | `MessageView`, `RacePanel`, `SettingsPanel`. |
| `wwwroot/` | The JavaScript modules, the vendored WebLLM library and the icon. |

`AiApp` is the manifest, `AiWindow` the window. `src/Kazo.Ai.Os` is a Kazo OS host with the app, to
run it locally; `tests/Kazo.Ai.Tests` holds the xUnit tests.

## Sending a message

`ChatSession.SendAsync` does, in order:

1. Adds the message to the conversation (a new one takes its title from the first line).
2. Builds the messages: the persona's system prompt, then the conversation (failed answers left out).
3. **AutoTune** chooses the sampling parameters (`AutoTuner.Tune`), from the detected context, the
   strategy, and what the ratings taught when learning is on.
4. Runs the **request part of the enabled modules**, in their order.
5. Chooses the model: the selected one, or the local model when the selected service needs a key
   that was never given (setting `preferLocalWithoutKey`).
6. Single mode: streams the answer from the provider. Race mode: `ModelRace` asks every race model
   at once, scores each answer when it ends, and keeps the best.
7. Runs the **response part of the enabled modules** on the answer (in a race, on each answer
   before it is scored).
8. Saves the conversation. A failure is kept on the answer with its kind (`errorKind`), which the
   window explains; Stop keeps what was written.

`ChatSession` has no Blazor dependency: the tests drive it with fake providers, an in-memory file
system and an in-memory key store. It raises `Changed` for every change; the window redraws at most
every 60 ms while an answer streams.

## Network

Kazo OS gives apps a real network through its optional server only, but AI services must be
reachable with no server. `BrowserStreamingHttp` therefore calls the browser's `fetch` through
`wwwroot/js/ai-fetch.js` and pumps the body back to C# piece by piece. Before each request it checks
that the app holds `network.real.access` (a dangerous capability that an administrator approves);
without it, the request fails with `NotAllowed` and only an already downloaded local model can
answer. Services must allow CORS: OpenRouter, OpenAI, and Ollama or LM Studio with CORS turned on
(`OLLAMA_ORIGINS`, LM Studio's "Enable CORS").

## Local model

`WebLlmEngine` drives WebLLM (Apache-2.0, vendored in `wwwroot/lib/web-llm/`) through
`wwwroot/js/webllm-bridge.js`, in a Web Worker (`webllm-worker.js`) so the window stays responsive.
The weights come from Hugging Face and are kept in the browser's Cache Storage; the window shows the
download progress. At start, when WebGPU is present and the model is already cached, it is loaded
right away (setting `autoLoadLocal`).

## Storage

Everything lives in the app's private folder, `~/.config/ca.kazo.ai/`:

| File | Content |
|---|---|
| `settings.json` | `AiSettings` (no key). |
| `feedback.json` | The ratings and what AutoTune learned. |
| `conversations/<id>.json` | One conversation (the list shows the 300 most recent). |
| `modules/<id>.json` | One imported JSON module. |

API keys are in the Kazo OS keyring (`KazoKeyring`, entry `provider/<service id>`), never in a file,
never exported. Every JSON goes through the source-generated `AiJsonContext`, so the app keeps
working when the OS host trims it.

## Security

- The Markdown of answers is HTML-encoded first; only the tags the renderer writes reach the page,
  and only `http` and `https` links are kept (`rel="noopener noreferrer"`).
- A JSON module's regular expressions run with a 250 ms timeout.
- An API key is sent only to the address of its own service.

## Kazo OS Store

The Store compiles `src/Kazo.Ai/Kazo.Ai.csproj` in Release and packs one assembly (Kazo-ca/Os#419):
CI checks that the package holds only `lib/Kazo.Ai.dll`. The listing comes from `store/`; the README
block is generated from it with `node .github/scripts/store-readme.mjs`.

The published Kazo OS trims the .NET framework (Kazo OS ADR 0242): when installing, the OS refuses a
package that calls a removed member. Version 0.1.1 was refused for `Regex.Replace` with a
`MatchEvaluator` or a static timeout, `Enumerable.Sum` over `double` and `ChannelWriter.WriteAsync`.
The app therefore uses `Regex.Matches`, `Regex.Replace(string, string)`, a loop and `TryWrite`, and
`TrimmedOsApiTests` keeps those calls out. Test a new version on a trimmed publish of the OS
(os.kazo.ca), not only with `dotnet run`, which keeps the whole framework.

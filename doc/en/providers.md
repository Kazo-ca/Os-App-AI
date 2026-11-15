> [Version française](../fr/providers.md)

# AI services, local model and data formats

## Services

Every service that speaks the OpenAI chat API (`POST <address>/chat/completions` with
`"stream": true`, `GET <address>/models`) works with no code. The presets:

| Id | Name | Address | Key | Enabled at first |
|---|---|---|---|---|
| `local` | Local (WebGPU) | in the browser | no | yes |
| `openrouter` | OpenRouter | `https://openrouter.ai/api/v1` | yes | yes |
| `openai` | OpenAI | `https://api.openai.com/v1` | yes | no |
| `ollama` | Ollama | `http://localhost:11434/v1` | no | no |
| `lmstudio` | LM Studio | `http://localhost:1234/v1` | no | no |

- An address must be `https://`, or `http://` on `localhost` only.
- The key is sent as `Authorization: Bearer <key>` to its service only. OpenRouter also gets
  `HTTP-Referer: https://os.kazo.ca/` and `X-Title: Kazo OS AI`.
- `top_k` and `repetition_penalty` are sent to every service except `api.openai.com`, which refuses
  them.
- The service is called from the browser: it must allow CORS. Ollama: start it with
  `OLLAMA_ORIGINS=*` (or the OS address); LM Studio: turn on "Enable CORS" in its server settings.
- A model is named `service:model`, for example `openrouter:openai/gpt-4o-mini` or
  `ollama:llama3.2`. The models offered in the toolbar are the ones typed in Settings > Services
  (one per line, or picked from "List the service's models") and the race models.

### Errors

A failure carries an `AiErrorKind`, kept on the answer as `errorKind` and explained by the window:

| Kind | Usual cause |
|---|---|
| `Auth` | 401, 403: wrong or missing key. |
| `Credits` | 402: no credit left. |
| `NotFound` | 404: unknown model or address. |
| `RateLimit` | 429: too many requests. |
| `Timeout` | 408, 504, or no answer in time. |
| `Network` | The browser could not reach the service (offline, wrong address, CORS). |
| `Server` | 5xx. |
| `NotAllowed` | The app does not hold `network.real.access`. |
| `Unavailable` | The service is not enabled, or WebGPU is missing for the local model. |
| `Unknown` | Anything else. |

## Local model

The local service runs a model in the browser with WebLLM over WebGPU. Settings > Local model lists
the models WebLLM offers (Qwen 2.5, Llama 3.x, Phi 3.5, Gemma 2, SmolLM2...), with the video memory
each needs; the default is `Qwen2.5-1.5B-Instruct-q4f16_1-MLC` (about 1.6 GB). The first load
downloads the weights from Hugging Face (`huggingface.co`, which needs `network.real.access`); they
stay in the browser's Cache Storage, and "Delete the download" frees them.

## Data formats

All files are JSON in `~/.config/ca.kazo.ai/`. Field names follow the common conventions of chat
tools (snake_case for sampling parameters and race events), so the data can be read or produced by
other tools.

### Sampling parameters

```json
{ "temperature": 0.7, "top_p": 0.9, "top_k": 50, "frequency_penalty": 0.1, "presence_penalty": 0.1, "repetition_penalty": 1.0 }
```

Ranges: temperature 0–2, top_p 0–1, top_k 0–200 (0: off), frequency and presence penalties −2 to 2,
repetition_penalty 0–2.

### AutoTune result (`autotune` of an answer)

```json
{
  "params": { "temperature": 0.35, "top_p": 0.85, "...": 0 },
  "detectedContext": "code",
  "confidence": 0.72,
  "reasoning": "Detected code context (72%).",
  "contextScores": [ { "type": "code", "score": 9.5, "percentage": 72 } ],
  "paramDeltas": [ { "param": "temperature", "before": 0.3, "after": 0.35, "delta": 0.05, "reason": "learned from ratings" } ],
  "baselineParams": { "temperature": 0.3, "...": 0 }
}
```

- Context types: `code`, `creative`, `analytical`, `conversational`, `chaotic`.
- Strategies (`strategy` in the settings): `adaptive` (from the context), `precise`, `balanced`,
  `creative`, `chaotic`, and `manual` (`manualParams`).

### Ratings (`feedback.json`)

```json
{
  "history": [
    { "messageId": "…", "timestamp": 1763200000000, "contextType": "code", "model": "openrouter:openai/gpt-4o-mini",
      "persona": "developer", "params": { "temperature": 0.35, "...": 0 }, "rating": 1,
      "heuristics": { "responseLength": 812, "repetitionScore": 0.02, "averageSentenceLength": 14.2, "vocabularyDiversity": 0.61 } }
  ],
  "learnedProfiles": {
    "code": { "contextType": "code", "sampleCount": 6, "positiveCount": 5, "negativeCount": 1,
              "positiveParams": { "...": 0 }, "negativeParams": { "...": 0 }, "lastUpdated": 1763200000000 }
  }
}
```

The profiles are exponential moving averages (weight 0.3) of the parameters of good and bad answers.
They start to count after 3 ratings of a context, and move the parameters at most halfway (reached
at 20 ratings) toward the good ones and away from the bad ones. The history keeps 500 ratings.

### Race (`race` of an answer)

```json
{
  "models_queried": 3,
  "models_succeeded": 2,
  "total_duration_ms": 4210,
  "rankings": [
    { "model": "openrouter:anthropic/claude-3.5-haiku", "score": 78, "duration_ms": 3100, "success": true, "error": null, "content_length": 1290 }
  ]
}
```

While a race runs, each model that ends gives a result
`{ "model", "score", "duration_ms", "success", "error", "content_length", "models_responded", "models_total" }`,
and the leader changes when a new answer beats it by at least `raceMinDelta` points (default 8).
The score (0–100) is the app's own heuristic: substance, structure, relevance to the question,
variety, minus open code blocks. A C# extension can replace it (`ModuleRegistry.Scorer`).

### Settings (`settings.json`)

| Field | Meaning |
|---|---|
| `providers` | The services: `id`, `name`, `kind` (`openai-compatible` or `webllm`), `baseUrl`, `enabled`, `needsKey`, `models`. |
| `selectedModel` | The model of single mode (`service:model`). |
| `mode` | `single` or `race`. |
| `raceModels`, `raceMinDelta` | The race (2 to 8 models). |
| `strategy`, `manualParams`, `learnFromFeedback`, `maxTokens` | AutoTune and the answer length. |
| `enabledModules`, `moduleConfigs` | The modules on, and their own settings. |
| `personaId`, `personas` | The persona of new conversations, and the person's own personas (`id`, `name`, `emoji`, `systemPrompt`). |
| `localModel`, `autoLoadLocal`, `preferLocalWithoutKey` | The local model. |

The export (Settings > Import / export) is `{ "format": "kazo-ai-settings", "exportedAt", "settings", "modules" }`,
with the JSON modules and without any key.

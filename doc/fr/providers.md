> [English version](../en/providers.md)

# Services d'IA, modèle local et formats de données

## Services

Tout service qui parle l'API de chat d'OpenAI (`POST <adresse>/chat/completions` avec
`"stream": true`, `GET <adresse>/models`) fonctionne sans code. Les préréglages :

| Id | Nom | Adresse | Clé | Actif au départ |
|---|---|---|---|---|
| `local` | Local (WebGPU) | dans le navigateur | non | oui |
| `openrouter` | OpenRouter | `https://openrouter.ai/api/v1` | oui | oui |
| `openai` | OpenAI | `https://api.openai.com/v1` | oui | non |
| `ollama` | Ollama | `http://localhost:11434/v1` | non | non |
| `lmstudio` | LM Studio | `http://localhost:1234/v1` | non | non |

- Une adresse doit être en `https://`, ou en `http://` sur `localhost` seulement.
- La clé est envoyée en `Authorization: Bearer <clé>` à son seul service. OpenRouter reçoit aussi
  `HTTP-Referer: https://os.kazo.ca/` et `X-Title: Kazo OS AI`.
- `top_k` et `repetition_penalty` sont envoyés à tous les services sauf `api.openai.com`, qui les
  refuse.
- Le service est appelé depuis le navigateur : il doit accepter CORS. Ollama : le lancer avec
  `OLLAMA_ORIGINS=*` (ou l'adresse de l'OS) ; LM Studio : activer « Enable CORS » dans les réglages
  de son serveur.
- Un modèle se nomme `service:modèle`, par exemple `openrouter:openai/gpt-4o-mini` ou
  `ollama:llama3.2`. Les modèles proposés dans la barre d'outils sont ceux saisis dans Réglages >
  Services (un par ligne, ou choisis avec « Lister les modèles du service ») et ceux de la course.

### Erreurs

Un échec porte un `AiErrorKind`, gardé sur la réponse dans `errorKind` et expliqué par la fenêtre :

| Type | Cause habituelle |
|---|---|
| `Auth` | 401, 403 : clé fausse ou absente. |
| `Credits` | 402 : plus de crédit. |
| `NotFound` | 404 : modèle ou adresse inconnus. |
| `RateLimit` | 429 : trop de requêtes. |
| `Timeout` | 408, 504, ou pas de réponse à temps. |
| `Network` | Le navigateur n'a pas pu joindre le service (hors ligne, mauvaise adresse, CORS). |
| `Server` | 5xx. |
| `NotAllowed` | L'app ne détient pas `network.real.access`. |
| `Unavailable` | Le service n'est pas actif, ou WebGPU manque pour le modèle local. |
| `Unknown` | Tout le reste. |

## Modèle local

Le service local fait tourner un modèle dans le navigateur avec WebLLM sur WebGPU. Réglages > Modèle
local liste les modèles que WebLLM propose (Qwen 2.5, Llama 3.x, Phi 3.5, Gemma 2, SmolLM2...), avec
la mémoire vidéo que chacun demande ; celui par défaut est `Qwen2.5-1.5B-Instruct-q4f16_1-MLC`
(environ 1,6 Go). Le premier chargement télécharge les poids depuis Hugging Face (`huggingface.co`,
qui demande `network.real.access`) ; ils restent dans le Cache Storage du navigateur, et
« Supprimer le téléchargement » les libère.

## Formats de données

Tous les fichiers sont en JSON dans `~/.config/ca.kazo.ai/`. Les noms de champs suivent les
conventions courantes des outils de discussion (snake_case pour les paramètres d'échantillonnage et
les événements de course), pour que d'autres outils puissent lire ou produire ces données.

### Paramètres d'échantillonnage

```json
{ "temperature": 0.7, "top_p": 0.9, "top_k": 50, "frequency_penalty": 0.1, "presence_penalty": 0.1, "repetition_penalty": 1.0 }
```

Plages : temperature 0–2, top_p 0–1, top_k 0–200 (0 : désactivé), pénalités de fréquence et de
présence −2 à 2, repetition_penalty 0–2.

### Résultat d'AutoTune (`autotune` d'une réponse)

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

- Types de contexte : `code`, `creative`, `analytical`, `conversational`, `chaotic`.
- Stratégies (`strategy` dans les réglages) : `adaptive` (selon le contexte), `precise`, `balanced`,
  `creative`, `chaotic`, et `manual` (`manualParams`).

### Notes (`feedback.json`)

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

Les profils sont des moyennes mobiles exponentielles (poids 0,3) des paramètres des bonnes et des
mauvaises réponses. Ils comptent à partir de 3 notes d'un contexte, et déplacent les paramètres au
plus à mi-chemin (atteint à 20 notes) vers les bons et à l'opposé des mauvais. L'historique garde 500
notes.

### Course (`race` d'une réponse)

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

Pendant une course, chaque modèle qui termine donne un résultat
`{ "model", "score", "duration_ms", "success", "error", "content_length", "models_responded", "models_total" }`,
et le meneur change quand une nouvelle réponse le dépasse d'au moins `raceMinDelta` points (8 par
défaut). La note (0–100) est l'heuristique propre de l'app : substance, structure, pertinence par
rapport à la question, variété, moins les blocs de code non fermés. Une extension C# peut la
remplacer (`ModuleRegistry.Scorer`).

### Réglages (`settings.json`)

| Champ | Sens |
|---|---|
| `providers` | Les services : `id`, `name`, `kind` (`openai-compatible` ou `webllm`), `baseUrl`, `enabled`, `needsKey`, `models`. |
| `selectedModel` | Le modèle du mode simple (`service:modèle`). |
| `mode` | `single` ou `race`. |
| `raceModels`, `raceMinDelta` | La course (2 à 8 modèles). |
| `strategy`, `manualParams`, `learnFromFeedback`, `maxTokens` | AutoTune et la longueur des réponses. |
| `enabledModules`, `moduleConfigs` | Les modules actifs, et leurs réglages propres. |
| `personaId`, `personas` | La persona des nouvelles conversations, et les personas de la personne (`id`, `name`, `emoji`, `systemPrompt`). |
| `localModel`, `autoLoadLocal`, `preferLocalWithoutKey` | Le modèle local. |

L'export (Réglages > Import / export) est `{ "format": "kazo-ai-settings", "exportedAt", "settings", "modules" }`,
avec les modules JSON et sans aucune clé.

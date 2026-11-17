> [English version](../en/architecture.md)

# Architecture de Kazo IA

Kazo IA est une app fenêtrée de Kazo OS (`ca.kazo.ai`), un seul assembly `Kazo.Ai.dll`, écrite en C#
et Blazor. Le JavaScript se limite à ce que le C# ne peut pas atteindre dans le navigateur : `fetch`
avec un corps lu en flux, WebGPU (le modèle local), le défilement et la touche Entrée de la zone de
saisie.

## Dossiers de `src/Kazo.Ai`

| Dossier | Rôle |
|---|---|
| `Chat/` | Les données d'une discussion (`ChatMessage`, `ChatRequest`, `Conversation`) et `ChatSession`, la discussion sans sa fenêtre. |
| `Http/` | `IStreamingHttp`, le lecteur d'événements serveur (`SseParser`), les erreurs (`AiServiceException`, `AiErrorKind`) et `BrowserStreamingHttp`, le pont fetch. |
| `Providers/` | `IChatProvider`, le fournisseur compatible OpenAI, le fournisseur local WebGPU (`WebLlmProvider`, `WebLlmEngine`), les réglages et préréglages des services, `ProviderFactory`. |
| `Race/` | `ModelRace` (plusieurs modèles en parallèle), le noteur (`IResponseScorer`, `HeuristicResponseScorer`) et les résultats de course. |
| `Tuning/` | AutoTune : `ContextDetector`, `AutoTuner`, `FeedbackLearner` (apprentissage par 👍 et 👎). |
| `Modules/` | `IChatModule`, `ModuleRegistry`, les modules intégrés et les modules JSON (`DeclarativeModule`). |
| `Extensions/` | L'endroit des modules C# compilés dans l'app ([modules.md](modules.md)). |
| `Settings/` | `AiSettings`, les personas, le magasin de clés d'API (trousseau de l'OS), l'export des réglages. |
| `Storage/` | `IAiFileSystem` (le dossier privé de l'app) et `AiStore` (réglages, conversations, notes, modules). |
| `Markdown/` | `MarkdownRenderer` : le Markdown des réponses en HTML sûr. |
| `Components/` | `MessageView`, `RacePanel`, `SettingsPanel`. |
| `wwwroot/` | Les modules JavaScript, la bibliothèque WebLLM embarquée et l'icône. |

`AiApp` est le manifeste, `AiWindow` la fenêtre. `src/Kazo.Ai.Os` est un hôte Kazo OS avec l'app,
pour la lancer en local ; `tests/Kazo.Ai.Tests` contient les tests xUnit.

## Envoyer un message

`ChatSession.SendAsync` fait, dans l'ordre :

1. Ajoute le message à la conversation (une nouvelle prend son titre de la première ligne).
2. Construit les messages : le prompt système de la persona, puis la conversation (sans les réponses
   en échec).
3. **AutoTune** choisit les paramètres d'échantillonnage (`AutoTuner.Tune`), à partir du contexte
   détecté, de la stratégie, et de ce que les notes ont appris quand l'apprentissage est actif.
4. Exécute la **partie requête des modules actifs**, dans leur ordre.
5. Choisit le modèle : celui sélectionné, ou le modèle local quand le service sélectionné demande une
   clé jamais donnée (réglage `preferLocalWithoutKey`).
6. Mode simple : lit la réponse du fournisseur en flux. Mode course : `ModelRace` interroge tous les
   modèles de la course à la fois, note chaque réponse quand elle se termine et garde la meilleure.
7. Exécute la **partie réponse des modules actifs** sur la réponse (dans une course, sur chaque
   réponse avant qu'elle soit notée).
8. Enregistre la conversation. Un échec est gardé sur la réponse avec son type (`errorKind`), que la
   fenêtre explique ; Arrêter garde ce qui a été écrit.

`ChatSession` ne dépend pas de Blazor : les tests la pilotent avec de faux fournisseurs, un système
de fichiers en mémoire et un magasin de clés en mémoire. Elle lève `Changed` à chaque changement ; la
fenêtre se redessine au plus toutes les 60 ms pendant qu'une réponse arrive.

## Réseau

Kazo OS ne donne le réseau réel aux apps que par son serveur optionnel, or les services d'IA doivent
être joignables sans serveur. `BrowserStreamingHttp` appelle donc le `fetch` du navigateur par
`wwwroot/js/ai-fetch.js` et renvoie le corps au C# morceau par morceau. Avant chaque requête, il
vérifie que l'app détient `network.real.access` (une capacité dangereuse qu'un administrateur
approuve) ; sans elle, la requête échoue avec `NotAllowed` et seul un modèle local déjà téléchargé
peut répondre. Les services doivent accepter CORS : OpenRouter, OpenAI, et Ollama ou LM Studio avec
CORS activé (`OLLAMA_ORIGINS`, « Enable CORS » de LM Studio).

## Modèle local

`WebLlmEngine` pilote WebLLM (Apache-2.0, embarqué dans `wwwroot/lib/web-llm/`) par
`wwwroot/js/webllm-bridge.js`, dans un Web Worker (`webllm-worker.js`) pour que la fenêtre reste
fluide. Les poids viennent de Hugging Face et sont gardés dans le Cache Storage du navigateur ; la
fenêtre montre la progression du téléchargement. Au démarrage, si WebGPU est présent et le modèle
déjà en cache, il est chargé tout de suite (réglage `autoLoadLocal`).

## Stockage

Tout est dans le dossier privé de l'app, `~/.config/ca.kazo.ai/` :

| Fichier | Contenu |
|---|---|
| `settings.json` | `AiSettings` (aucune clé). |
| `feedback.json` | Les notes et ce qu'AutoTune en a appris. |
| `conversations/<id>.json` | Une conversation (la liste montre les 300 plus récentes). |
| `modules/<id>.json` | Un module JSON importé. |

Les clés d'API sont dans le trousseau de Kazo OS (`KazoKeyring`, entrée `provider/<id du service>`),
jamais dans un fichier, jamais exportées. Tout le JSON passe par `AiJsonContext` (génération de
source), pour que l'app fonctionne encore quand l'hôte de l'OS l'élague (trimming).

## Sécurité

- Le Markdown des réponses est d'abord encodé en HTML ; seules les balises écrites par le moteur de
  rendu arrivent dans la page, et seuls les liens `http` et `https` sont gardés
  (`rel="noopener noreferrer"`).
- Les expressions régulières d'un module JSON ont un délai maximal de 250 ms.
- Une clé d'API n'est envoyée qu'à l'adresse de son propre service.

## Kazo OS Store

Le Store compile `src/Kazo.Ai/Kazo.Ai.csproj` en Release et emballe un seul assembly
(Kazo-ca/Os#419) : la CI vérifie que le paquet ne contient que `lib/Kazo.Ai.dll`. La fiche vient de
`store/` ; le bloc du README en est généré avec `node .github/scripts/store-readme.mjs`.

Kazo OS publié élague le framework .NET (ADR 0242 de Kazo OS) : à l'installation, l'OS refuse un
paquet qui appelle un membre retiré. La version 0.1.1 a été refusée pour `Regex.Replace` avec un
`MatchEvaluator` ou un délai statique, `Enumerable.Sum` sur des `double` et `ChannelWriter.WriteAsync`.
L'app passe donc par `Regex.Matches`, `Regex.Replace(string, string)`, une boucle et `TryWrite`, et
`TrimmedOsApiTests` empêche ces appels de revenir. Tester une nouvelle version sur une publication
rognée de l'OS (os.kazo.ca), pas seulement sur `dotnet run`, qui garde le framework entier.

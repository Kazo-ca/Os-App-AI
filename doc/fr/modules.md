> [English version](../en/modules.md)

# Les modules de Kazo IA

Un module change la discussion à deux moments : la **requête**, avant son départ (message système,
paramètres d'échantillonnage, messages), et la **réponse**, avant son affichage (et, dans une course,
avant qu'elle soit notée). La personne active et désactive les modules dans Réglages > Modules ; les
modules actifs s'exécutent dans l'ordre de leur `order` (le plus petit d'abord).

Il y en a trois sortes :

| Sorte | Où | Peut faire |
|---|---|---|
| Intégré | `src/Kazo.Ai/Modules/BuiltInModules.cs` | `concise`, `cite_uncertainty`, `same_language` (consignes ajoutées au message système), `preamble_trimmer`, `whitespace_tidier` (nettoient la réponse). |
| JSON | `~/.config/ca.kazo.ai/modules/<id>.json`, importé depuis les réglages | Ajouter du texte au message système, fixer des paramètres d'échantillonnage, remplacer du texte dans les réponses par expressions régulières. Sans code. |
| C# | `src/Kazo.Ai/Extensions/` | Tout : une classe qui implémente `IChatModule`, compilée dans l'app. |

Un id est unique : un module JSON dont l'id est déjà pris par un module intégré ou C# est refusé, et
la raison s'affiche dans Réglages > Modules.

## Modules JSON

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

| Champ | Obligatoire | Sens |
|---|---|---|
| `id` | oui | Lettres minuscules, chiffres, `_` ou `-` (64 au plus). C'est aussi le nom du fichier. |
| `name`, `description`, `version`, `author` | non | Affichés dans les réglages. `version` vaut `1.0.0` par défaut. |
| `order` | non | Ordre d'exécution, 500 par défaut. |
| `systemPrompt` | non | Texte pour le message système. |
| `systemPromptPosition` | non | `append` (par défaut, après), `prepend` (avant) ou `replace` (remplace le prompt de la persona). |
| `params` | non | Paramètres d'échantillonnage imposés : `temperature`, `top_p`, `top_k`, `frequency_penalty`, `presence_penalty`, `repetition_penalty`. Ils l'emportent sur AutoTune. |
| `replacements` | non | Appliqués à la réponse dans l'ordre. `pattern` est une expression régulière .NET (250 ms au plus par réponse), `replacement` peut utiliser `$1` ; `flags` : `i` sans casse, `m` multiligne, `s` le point couvre les sauts de ligne. |

Un fichier invalide est refusé avec sa raison (Réglages > Modules). Les modules JSON font partie de
l'export des réglages.

## Modules C#

Une app Kazo OS installée est un seul assembly (Kazo-ca/Os#419) : les modules C# sont donc compilés
dans `Kazo.Ai.dll`. Ajoutez un fichier dans `src/Kazo.Ai/Extensions/` :

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

- `ChatRequest` est un record : renvoyez une copie modifiée (`with`, `WithSystem`).
- `ModuleContext` donne l'id de la conversation, l'id de la persona, l'id du service (`race` en mode
  course) et les réglages propres au module (`moduleConfigs` dans `settings.json`, un dictionnaire de
  chaînes).
- `TransformResponse(input, config) => output` a la forme des « modules de transformation
  sémantique » de nombreux outils de discussion : un tel module se porte en recopiant le corps de sa
  fonction en C#.
- `RegisterExtensions` s'exécute une fois à l'ouverture de la fenêtre. Une exception lancée par un
  module fait échouer la réponse avec son message ; gardez les modules rapides (ils s'exécutent à
  chaque réponse, pour chaque modèle d'une course).
- Tests : `tests/Kazo.Ai.Tests` montre comment appeler un module et le registre sans fenêtre.

## Une nouvelle sorte de service

Les services qui parlent l'API de chat d'OpenAI ne demandent aucun code (Réglages > Services >
Ajouter). Pour une autre API, implémentez `IChatProvider` (`ListModelsAsync`, `StreamAsync` qui
renvoie des morceaux `ChatDelta`), ajoutez un `ProviderKind` et son cas dans
`ProviderFactory.Create`, et levez `AiServiceException` avec le bon `AiErrorKind` pour que la fenêtre
explique les échecs. Voir [providers.md](providers.md).

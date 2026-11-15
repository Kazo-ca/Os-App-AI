using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kazo.Ai.Chat;

namespace Kazo.Ai.Modules;

/// <summary>
/// A module written as a JSON file, with no code: dropped in <c>~/.config/ca.kazo.ai/modules/</c>
/// (or imported from the settings), it can add text to the system message and replace text in the
/// answers with regular expressions. The format is in doc/en/extending.md.
/// </summary>
public sealed class DeclarativeModuleDefinition
{
    /// <summary>Gets or sets the id (<c>snake_case</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>Gets or sets the author.</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    /// <summary>Gets or sets the order (smaller first).</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; } = 500;

    /// <summary>Gets or sets text added to the system message.</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    /// <summary>Gets or sets where it goes: <c>append</c> (default), <c>prepend</c> or <c>replace</c>.</summary>
    [JsonPropertyName("systemPromptPosition")]
    public string SystemPromptPosition { get; set; } = "append";

    /// <summary>Gets or sets the replacements applied to the answers, in order.</summary>
    [JsonPropertyName("replacements")]
    public List<DeclarativeReplacement> Replacements { get; set; } = [];

    /// <summary>Gets or sets the sampling parameters forced by the module (any subset).</summary>
    [JsonPropertyName("params")]
    public Dictionary<string, double>? Params { get; set; }
}

/// <summary>One replacement of a <see cref="DeclarativeModuleDefinition"/>.</summary>
public sealed class DeclarativeReplacement
{
    /// <summary>Gets or sets the .NET regular expression.</summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Gets or sets the replacement (<c>$1</c> for a group).</summary>
    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;

    /// <summary>Gets or sets the flags: <c>i</c> (ignore case), <c>m</c> (multiline), <c>s</c> (dot matches newlines).</summary>
    [JsonPropertyName("flags")]
    public string Flags { get; set; } = string.Empty;
}

/// <summary>A <see cref="DeclarativeModuleDefinition"/> turned into a module.</summary>
public sealed class DeclarativeModule : IChatModule
{
    /// <summary>The time a replacement may take on one answer (a bad pattern must not freeze the app).</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly DeclarativeModuleDefinition _definition;
    private readonly IReadOnlyList<(Regex Regex, string Replacement)> _replacements;

    private DeclarativeModule(DeclarativeModuleDefinition definition, IReadOnlyList<(Regex, string)> replacements)
    {
        _definition = definition;
        _replacements = replacements;
    }

    /// <inheritdoc />
    public string Id => _definition.Id;

    /// <inheritdoc />
    public string Name => _definition.Name.Length > 0 ? _definition.Name : _definition.Id;

    /// <inheritdoc />
    public string Description => _definition.Description;

    /// <inheritdoc />
    public string Version => _definition.Version;

    /// <inheritdoc />
    public string Author => _definition.Author;

    /// <inheritdoc />
    public int Order => _definition.Order;

    /// <inheritdoc />
    public ModuleStage Stages =>
        (HasRequestPart ? ModuleStage.Request : ModuleStage.None) | (_replacements.Count > 0 ? ModuleStage.Response : ModuleStage.None);

    /// <summary>Gets the definition (to export it).</summary>
    public DeclarativeModuleDefinition Definition => _definition;

    private bool HasRequestPart => !string.IsNullOrWhiteSpace(_definition.SystemPrompt) || _definition.Params is { Count: > 0 };

    /// <summary>Reads and checks a module file.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The module.</returns>
    /// <exception cref="FormatException">The file is not a valid module; the message says why.</exception>
    public static DeclarativeModule Parse(string json)
    {
        DeclarativeModuleDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize(json, AiJsonContext.Default.DeclarativeModuleDefinition);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The module is not valid JSON: " + exception.Message, exception);
        }

        if (definition is null || !ModuleRegistry.IsValidId(definition.Id))
        {
            throw new FormatException("The module needs an \"id\" of lower-case letters, digits, '_' or '-'.");
        }

        if (definition.SystemPromptPosition is not ("append" or "prepend" or "replace"))
        {
            throw new FormatException("\"systemPromptPosition\" must be append, prepend or replace.");
        }

        if (definition.Params is { } p)
        {
            foreach (string name in p.Keys)
            {
                if (!SamplingParameters.Names.Contains(name))
                {
                    throw new FormatException($"Unknown parameter \"{name}\" in \"params\".");
                }
            }
        }

        var replacements = new List<(Regex, string)>();
        foreach (DeclarativeReplacement replacement in definition.Replacements)
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            foreach (char flag in replacement.Flags)
            {
                options |= flag switch
                {
                    'i' => RegexOptions.IgnoreCase,
                    'm' => RegexOptions.Multiline,
                    's' => RegexOptions.Singleline,
                    _ => throw new FormatException($"Unknown flag '{flag}' (use i, m or s)."),
                };
            }

            try
            {
                replacements.Add((new Regex(replacement.Pattern, options, RegexTimeout), replacement.Replacement));
            }
            catch (ArgumentException exception)
            {
                throw new FormatException($"The pattern \"{replacement.Pattern}\" is not valid: {exception.Message}", exception);
            }
        }

        return new DeclarativeModule(definition, replacements);
    }

    /// <inheritdoc />
    public ValueTask<ChatRequest> TransformRequestAsync(ChatRequest request, ModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ChatRequest result = request;
        if (!string.IsNullOrWhiteSpace(_definition.SystemPrompt))
        {
            string current = request.SystemText;
            string added = _definition.SystemPrompt.Trim();
            result = result.WithSystem(_definition.SystemPromptPosition switch
            {
                "replace" => added,
                "prepend" => current.Length == 0 ? added : added + "\n\n" + current,
                _ => current.Length == 0 ? added : current + "\n\n" + added,
            });
        }

        if (_definition.Params is { Count: > 0 } forced)
        {
            SamplingParameters parameters = result.Parameters;
            foreach ((string name, double value) in forced)
            {
                parameters = parameters.With(name, value);
            }

            result = result with { Parameters = parameters.Clamp() };
        }

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public string TransformResponse(string input, IReadOnlyDictionary<string, string>? config)
    {
        ArgumentNullException.ThrowIfNull(input);
        string output = input;
        foreach ((Regex regex, string replacement) in _replacements)
        {
            try
            {
                output = regex.Replace(output, replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pattern too slow on this answer is skipped, the others still apply.
            }
        }

        return output;
    }
}

using System.Text.RegularExpressions;
using Kazo.Ai.Chat;

namespace Kazo.Ai.Modules;

/// <summary>
/// The modules shipped with the app. They are plain helpers written for it: two answer cleaners
/// and three instructions added to the system text.
/// </summary>
public static partial class BuiltInModules
{
    /// <summary>Every built-in module.</summary>
    public static IReadOnlyList<IChatModule> All { get; } =
    [
        new InstructionModule("concise", "Concise answers", "Asks the model to answer briefly, without filler.", 100,
            "Answer concisely. Skip introductions and conclusions that repeat the question."),
        new InstructionModule("cite_uncertainty", "Say when unsure", "Asks the model to say clearly when it is not sure, instead of guessing.", 110,
            "When you are not sure of a fact, say so plainly and explain what would confirm it."),
        new InstructionModule("same_language", "Answer in my language", "Asks the model to answer in the language of the last message.", 120,
            "Always answer in the language of the user's last message."),
        new PreambleTrimmer(),
        new WhitespaceTidier(),
    ];

    /// <summary>A module that adds a sentence to the system text.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="description">Its description.</param>
    /// <param name="order">Its order.</param>
    /// <param name="instruction">The sentence.</param>
    public sealed class InstructionModule(string id, string name, string description, int order, string instruction) : IChatModule
    {
        /// <inheritdoc />
        public string Id => id;

        /// <inheritdoc />
        public string Name => name;

        /// <inheritdoc />
        public string Description => description;

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "Kazo OS";

        /// <inheritdoc />
        public ModuleStage Stages => ModuleStage.Request;

        /// <inheritdoc />
        public int Order => order;

        /// <summary>Gets the sentence added.</summary>
        public string Instruction => instruction;

        /// <inheritdoc />
        public ValueTask<ChatRequest> TransformRequestAsync(ChatRequest request, ModuleContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            string system = request.SystemText;
            return ValueTask.FromResult(request.WithSystem(system.Length == 0 ? instruction : system + "\n\n" + instruction));
        }
    }

    /// <summary>
    /// Removes an opening line that only announces the answer ("Sure! Here is…", "Bien sûr ! Voici…")
    /// when real content follows it.
    /// </summary>
    public sealed partial class PreambleTrimmer : IChatModule
    {
        /// <inheritdoc />
        public string Id => "preamble_trimmer";

        /// <inheritdoc />
        public string Name => "Skip the preamble";

        /// <inheritdoc />
        public string Description => "Removes an opening line such as \"Sure! Here is…\" when the answer follows it.";

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "Kazo OS";

        /// <inheritdoc />
        public ModuleStage Stages => ModuleStage.Response;

        /// <inheritdoc />
        public int Order => 300;

        /// <inheritdoc />
        public string TransformResponse(string input, IReadOnlyDictionary<string, string>? config)
        {
            ArgumentNullException.ThrowIfNull(input);
            string trimmed = input.TrimStart();
            int newline = trimmed.IndexOf('\n', StringComparison.Ordinal);
            if (newline <= 0)
            {
                return input;
            }

            string first = trimmed[..newline];
            string rest = trimmed[(newline + 1)..].TrimStart('\n', '\r');
            return first.Length <= 120 && rest.Length > 0 && Preamble().IsMatch(first) ? rest : input;
        }

        [GeneratedRegex(@"^\s*(sure|certainly|of course|absolutely|great question|bien sûr|certainement|absolument|avec plaisir|bonne question)\b[^\n]*(here|voici|voilà|below|ci-dessous)?[^\n]*[:!.]\s*$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
        private static partial Regex Preamble();
    }

    /// <summary>Removes trailing spaces and runs of more than one empty line.</summary>
    public sealed partial class WhitespaceTidier : IChatModule
    {
        /// <inheritdoc />
        public string Id => "whitespace_tidier";

        /// <inheritdoc />
        public string Name => "Tidy spacing";

        /// <inheritdoc />
        public string Description => "Removes trailing spaces and extra empty lines (outside code blocks).";

        /// <inheritdoc />
        public string Version => "1.0.0";

        /// <inheritdoc />
        public string Author => "Kazo OS";

        /// <inheritdoc />
        public ModuleStage Stages => ModuleStage.Response;

        /// <inheritdoc />
        public int Order => 900;

        /// <inheritdoc />
        public string TransformResponse(string input, IReadOnlyDictionary<string, string>? config)
        {
            ArgumentNullException.ThrowIfNull(input);
            var output = new List<string>();
            bool inCode = false;
            int empty = 0;
            foreach (string raw in input.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                }

                string line = inCode ? raw : raw.TrimEnd();
                if (!inCode && line.Length == 0)
                {
                    empty++;
                    if (empty > 1)
                    {
                        continue;
                    }
                }
                else
                {
                    empty = 0;
                }

                output.Add(line);
            }

            return string.Join('\n', output).Trim('\n');
        }
    }
}

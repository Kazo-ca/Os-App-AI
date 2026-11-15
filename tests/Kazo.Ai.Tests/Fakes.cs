using System.Runtime.CompilerServices;
using Kazo.Ai.Chat;
using Kazo.Ai.Http;
using Kazo.Ai.Providers;

namespace Kazo.Ai.Tests;

/// <summary>A provider that answers with fixed pieces of text, or fails.</summary>
internal sealed class FakeProvider(string id, Func<ChatRequest, IReadOnlyList<string>> answer) : IChatProvider
{
    public string Id => id;

    public string Name => id;

    public List<ChatRequest> Requests { get; } = [];

    public AiServiceException? Failure { get; init; }

    public TimeSpan Delay { get; init; }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("m1", "Model 1")]);

    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Failure is not null)
        {
            throw Failure;
        }

        foreach (string piece in answer(request))
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatDelta(piece);
        }
    }

    public static FakeProvider Fixed(string id, params string[] pieces) => new(id, _ => pieces);
}

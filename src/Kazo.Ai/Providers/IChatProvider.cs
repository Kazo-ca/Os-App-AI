using Kazo.Ai.Chat;

namespace Kazo.Ai.Providers;

/// <summary>
/// A source of answers: an OpenAI-compatible service (OpenRouter, OpenAI, Ollama, LM Studio, any
/// server that speaks <c>/v1/chat/completions</c>) or the local model in the browser (WebLLM).
/// Adding another kind of provider means implementing this interface and registering it in
/// <see cref="ProviderFactory"/> (doc/en/extending.md).
/// </summary>
public interface IChatProvider
{
    /// <summary>Gets the id of the provider's settings (<see cref="ProviderSettings.Id"/>).</summary>
    string Id { get; }

    /// <summary>Gets the name shown.</summary>
    string Name { get; }

    /// <summary>Lists the models the provider offers.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The models, or an empty list when the provider cannot say.</returns>
    /// <exception cref="Http.AiServiceException">The provider refused or did not answer.</exception>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>Asks for an answer and reads it as it is written.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Stops the answer.</param>
    /// <returns>The pieces of the answer.</returns>
    /// <exception cref="Http.AiServiceException">The provider refused or did not answer.</exception>
    IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>A model offered by a provider.</summary>
/// <param name="Id">Its id at the provider.</param>
/// <param name="Name">Its name (the id when the provider gives none).</param>
/// <param name="ContextLength">Its context window in tokens, when known.</param>
public sealed record ModelInfo(string Id, string Name, int? ContextLength = null);

/// <summary>A model of a given provider: what the model selector and a race hold.</summary>
/// <param name="ProviderId">The provider's id.</param>
/// <param name="ModelId">The model's id at that provider.</param>
public sealed record ModelRef(string ProviderId, string ModelId)
{
    /// <summary>The text form <c>provider:model</c>, used in the settings file.</summary>
    public override string ToString() => ProviderId + ":" + ModelId;

    /// <summary>Reads the text form <c>provider:model</c> (the model id may hold more colons).</summary>
    /// <param name="text">The text.</param>
    /// <param name="model">The model, when the text is valid.</param>
    /// <returns>Whether it was valid.</returns>
    public static bool TryParse(string? text, out ModelRef? model)
    {
        model = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        model = new ModelRef(text[..colon], text[(colon + 1)..]);
        return true;
    }
}

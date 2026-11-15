namespace Kazo.Ai.Http;

/// <summary>
/// Sends an HTTP request to a real AI service and reads its answer as it arrives. The app uses
/// <see cref="BrowserStreamingHttp"/> (the browser's <c>fetch</c>, directly, without the Kazo
/// server); the tests use a fake. Kept behind this interface so that a Kazo OS SDK facade for direct
/// HTTP can replace it later without touching the providers (doc/en/architecture.md, "Network").
/// </summary>
public interface IStreamingHttp
{
    /// <summary>Sends a request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the request and stops reading the answer.</param>
    /// <returns>The response; its body is read with <see cref="StreamingHttpResponse.ReadChunksAsync"/>.</returns>
    /// <exception cref="AiServiceException">No answer (network down, address refused, CORS).</exception>
    Task<StreamingHttpResponse> SendAsync(StreamingHttpRequest request, CancellationToken cancellationToken);
}

/// <summary>An HTTP request to an AI service.</summary>
/// <param name="Method"><c>GET</c> or <c>POST</c>.</param>
/// <param name="Url">The absolute address.</param>
/// <param name="Headers">The headers (Authorization, Content-Type...).</param>
/// <param name="Body">The body, or null.</param>
public sealed record StreamingHttpRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body = null);

/// <summary>The answer of an AI service, whose body arrives in pieces.</summary>
public abstract class StreamingHttpResponse : IAsyncDisposable
{
    /// <summary>Gets the HTTP status.</summary>
    public abstract int Status { get; }

    /// <summary>Gets the <c>Content-Type</c> of the body, or an empty string.</summary>
    public abstract string ContentType { get; }

    /// <summary>Gets whether the status is 2xx.</summary>
    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>Reads the body as text pieces, as the network delivers them.</summary>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>The pieces.</returns>
    public abstract IAsyncEnumerable<string> ReadChunksAsync(CancellationToken cancellationToken);

    /// <summary>Reads the whole body.</summary>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>The body.</returns>
    public async Task<string> ReadAllAsync(CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder();
        await foreach (string chunk in ReadChunksAsync(cancellationToken))
        {
            text.Append(chunk);
        }

        return text.ToString();
    }

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

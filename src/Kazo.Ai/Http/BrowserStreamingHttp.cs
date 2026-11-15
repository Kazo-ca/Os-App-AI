using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Microsoft.JSInterop;

namespace Kazo.Ai.Http;

/// <summary>
/// <see cref="IStreamingHttp"/> on the browser's <c>fetch</c> (<c>wwwroot/js/ai-fetch.js</c>):
/// direct calls to the AI services, without the Kazo server, with the answer read as it arrives.
/// The service must allow the call (CORS): OpenRouter and OpenAI do; Ollama needs
/// <c>OLLAMA_ORIGINS</c>, LM Studio its "CORS" switch (doc/en/providers.md).
/// </summary>
/// <remarks>
/// The app declares <c>network.real.access</c> (dangerous): an administrator approves it when the app
/// is installed, and every request checks it is still granted, as the SDK's network facades do.
/// </remarks>
public sealed class BrowserStreamingHttp : IStreamingHttp, IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Ai/js/ai-fetch.js";

    private readonly IJSRuntime _js;
    private readonly IAppExecutionContext _context;
    private readonly ConcurrentDictionary<int, Channel<string>> _bodies = new();
    private readonly ConcurrentDictionary<int, string?> _errors = new();
    private DotNetObjectReference<BrowserStreamingHttp>? _self;
    private IJSObjectReference? _module;
    private int _nextId;

    /// <summary>Creates the transport.</summary>
    /// <param name="js">The page's JavaScript runtime.</param>
    /// <param name="context">The app's context (for the capability check).</param>
    public BrowserStreamingHttp(IJSRuntime js, IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(context);
        _js = js;
        _context = context;
    }

    /// <inheritdoc />
    public async Task<StreamingHttpResponse> SendAsync(StreamingHttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_context.GrantedCapabilities.Contains(AppCapabilities.NetworkRealAccess))
        {
            throw new AiServiceException(AiErrorKind.NotAllowed, "Kazo IA may not reach the Internet: the network.real.access permission was refused.");
        }

        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
        _self ??= DotNetObjectReference.Create(this);
        int id = Interlocked.Increment(ref _nextId);
        var body = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _bodies[id] = body;
        StartResult start;
        try
        {
            start = await _module.InvokeAsync<StartResult>("start", cancellationToken, _self, id, request.Method, request.Url, request.Headers, request.Body);
        }
        catch (OperationCanceledException)
        {
            await AbortAsync(id);
            throw;
        }

        if (start.Status == 0)
        {
            _bodies.TryRemove(id, out _);
            throw new AiServiceException(
                AiErrorKind.Network,
                $"No answer from {Host(request.Url)}: {start.Error}. The service may be down, or refuse calls from a web page (CORS).");
        }

        return new Response(this, id, start.Status, start.ContentType ?? string.Empty, body.Reader);
    }

    /// <summary>A piece of a body, from <c>ai-fetch.js</c>.</summary>
    /// <param name="id">The request.</param>
    /// <param name="text">The piece.</param>
    /// <returns>A task.</returns>
    [JSInvokable]
    public ValueTask OnChunk(int id, string text) =>
        _bodies.TryGetValue(id, out Channel<string>? body) ? body.Writer.WriteAsync(text) : ValueTask.CompletedTask;

    /// <summary>The end of a body, from <c>ai-fetch.js</c>.</summary>
    /// <param name="id">The request.</param>
    /// <param name="error">Why it stopped early, or null.</param>
    [JSInvokable]
    public void OnEnd(int id, string? error)
    {
        if (_bodies.TryRemove(id, out Channel<string>? body))
        {
            _errors[id] = error;
            body.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (Channel<string> body in _bodies.Values)
        {
            body.Writer.TryComplete();
        }

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone.
            }
        }

        _self?.Dispose();
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url;

    private async ValueTask AbortAsync(int id)
    {
        if (_bodies.TryRemove(id, out Channel<string>? body))
        {
            body.Writer.TryComplete();
        }

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("abort", id);
            }
            catch (JSException)
            {
                // Already finished.
            }
            catch (JSDisconnectedException)
            {
                // The page is gone.
            }
        }
    }

    /// <summary>What <c>start</c> returns.</summary>
    /// <param name="Status">The HTTP status, 0 without an answer.</param>
    /// <param name="ContentType">The Content-Type.</param>
    /// <param name="Error">Why there is no answer.</param>
    public sealed record StartResult(int Status, string? ContentType, string? Error);

    private sealed class Response(BrowserStreamingHttp owner, int id, int status, string contentType, ChannelReader<string> reader) : StreamingHttpResponse
    {
        private bool _ended;

        public override int Status => status;

        public override string ContentType => contentType;

        public override async IAsyncEnumerable<string> ReadChunksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using CancellationTokenRegistration registration = cancellationToken.Register(() => _ = owner.AbortAsync(id).AsTask());
            await foreach (string chunk in reader.ReadAllAsync(CancellationToken.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ended = true;
            if (owner._errors.TryRemove(id, out string? error) && error is not null)
            {
                throw new AiServiceException(AiErrorKind.Network, "The answer was cut: " + error);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_ended)
            {
                await owner.AbortAsync(id);
            }

            owner._errors.TryRemove(id, out _);
            await base.DisposeAsync();
        }
    }
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Kazo.Ai.Chat;
using Kazo.Ai.Http;
using Microsoft.JSInterop;

namespace Kazo.Ai.Providers;

/// <summary><see cref="ILocalModelEngine"/> on WebLLM (<c>wwwroot/js/webllm-bridge.js</c>).</summary>
public sealed class WebLlmEngine : ILocalModelEngine, IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Ai/js/webllm-bridge.js";

    private readonly IJSRuntime _js;
    private readonly ConcurrentDictionary<int, Channel<ChatDelta>> _answers = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private DotNetObjectReference<WebLlmEngine>? _self;
    private IJSObjectReference? _module;
    private LocalGpuInfo? _gpu;
    private int _nextId;

    /// <summary>Creates the engine.</summary>
    /// <param name="js">The page's JavaScript runtime.</param>
    public WebLlmEngine(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
        _js = js;
    }

    /// <inheritdoc />
    public event Action<double, string>? LoadProgress;

    /// <inheritdoc />
    public string? LoadedModel { get; private set; }

    /// <inheritdoc />
    public async Task<LocalGpuInfo> DetectAsync()
    {
        if (_gpu is not null)
        {
            return _gpu;
        }

        try
        {
            IJSObjectReference module = await ModuleAsync();
            _gpu = await module.InvokeAsync<LocalGpuInfo>("detect");
        }
        catch (JSException exception)
        {
            _gpu = new LocalGpuInfo(false, string.Empty, false, exception.Message);
        }

        return _gpu;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalModelInfo>> ListModelsAsync()
    {
        IJSObjectReference module = await ModuleAsync();
        return await module.InvokeAsync<LocalModelInfo[]>("listModels");
    }

    /// <inheritdoc />
    public async Task<bool> IsCachedAsync(string modelId)
    {
        IJSObjectReference module = await ModuleAsync();
        return await module.InvokeAsync<bool>("isCached", modelId);
    }

    /// <inheritdoc />
    public async Task LoadAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (LoadedModel == modelId)
            {
                return;
            }

            IJSObjectReference module = await ModuleAsync();
            _self ??= DotNetObjectReference.Create(this);
            try
            {
                // No time limit: a first download is gigabytes.
                await module.InvokeVoidAsync("load", CancellationToken.None, _self, modelId).AsTask().WaitAsync(cancellationToken);
            }
            catch (JSException exception)
            {
                throw new AiServiceException(AiErrorKind.Unavailable, $"The local model {modelId} could not load: {exception.Message}", innerException: exception);
            }

            LoadedModel = modelId;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatDelta> ChatAsync(string requestJson, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IJSObjectReference module = await ModuleAsync();
        _self ??= DotNetObjectReference.Create(this);
        int id = Interlocked.Increment(ref _nextId);
        var answer = Channel.CreateUnbounded<ChatDelta>(new UnboundedChannelOptions { SingleReader = true });
        _answers[id] = answer;
        await module.InvokeVoidAsync("chat", _self, id, requestJson);
        await using CancellationTokenRegistration registration = cancellationToken.Register(() => _ = StopAsync(module, id));
        await foreach (ChatDelta delta in answer.Reader.ReadAllAsync(CancellationToken.None))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delta;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async Task UnloadAsync()
    {
        IJSObjectReference module = await ModuleAsync();
        await module.InvokeVoidAsync("unload");
        LoadedModel = null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string modelId)
    {
        IJSObjectReference module = await ModuleAsync();
        await module.InvokeVoidAsync("remove", modelId);
        if (LoadedModel == modelId)
        {
            LoadedModel = null;
        }
    }

    /// <summary>Load progress, from the bridge.</summary>
    /// <param name="progress">0 to 1.</param>
    /// <param name="text">What WebLLM says.</param>
    [JSInvokable]
    public void OnLoadProgress(double progress, string text) => LoadProgress?.Invoke(progress, text);

    /// <summary>A piece of an answer, from the bridge.</summary>
    /// <param name="id">The request.</param>
    /// <param name="text">The piece.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The channel is unbounded, so <c>TryWrite</c> only fails once the answer is complete. Not
    /// <c>WriteAsync</c>: the trimmed framework of Kazo OS does not keep it.
    /// </remarks>
    [JSInvokable]
    public ValueTask OnChunk(int id, string text)
    {
        if (_answers.TryGetValue(id, out Channel<ChatDelta>? answer))
        {
            answer.Writer.TryWrite(new ChatDelta(text));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The end of an answer, from the bridge.</summary>
    /// <param name="id">The request.</param>
    /// <param name="error">Why it failed, or null.</param>
    /// <param name="usageJson">Token counts as <c>{"prompt","completion"}</c>, or null.</param>
    [JSInvokable]
    public void OnEnd(int id, string? error, string? usageJson)
    {
        if (!_answers.TryRemove(id, out Channel<ChatDelta>? answer))
        {
            return;
        }

        if (error is not null)
        {
            answer.Writer.TryComplete(new AiServiceException(AiErrorKind.Unavailable, "The local model failed: " + error));
            return;
        }

        TokenUsage? usage = null;
        if (usageJson is not null)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(usageJson);
                usage = new TokenUsage(
                    document.RootElement.TryGetProperty("prompt", out JsonElement p) && p.TryGetInt32(out int pt) ? pt : 0,
                    document.RootElement.TryGetProperty("completion", out JsonElement c) && c.TryGetInt32(out int ct) ? ct : 0);
            }
            catch (JsonException)
            {
                usage = null;
            }
        }

        answer.Writer.TryWrite(new ChatDelta(string.Empty, "stop", usage));
        answer.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("unload");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone.
            }
            catch (JSException)
            {
                // Nothing loaded.
            }
        }

        _self?.Dispose();
        _loadGate.Dispose();
    }

    private async Task StopAsync(IJSObjectReference module, int id)
    {
        if (_answers.TryRemove(id, out Channel<ChatDelta>? answer))
        {
            answer.Writer.TryComplete();
        }

        try
        {
            await module.InvokeVoidAsync("stop", id);
        }
        catch (JSException)
        {
            // Already done.
        }
        catch (JSDisconnectedException)
        {
            // The page is gone.
        }
    }

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);
}

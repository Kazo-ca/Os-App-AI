// Kazo IA: the local model, in the browser, on the GPU (WebLLM, Apache-2.0, vendored in
// ../lib/web-llm/). The model runs in a Web Worker (webllm-worker.js). C# (WebLlmEngine) decides
// which model, when to load it and what to send; this module only talks to WebLLM and passes the
// pieces of the answer back (OnChunk / OnEnd), like ai-fetch.js does for the remote services.
// The weights come from Hugging Face and the model code from GitHub, the first time only: WebLLM
// keeps them in the browser's Cache Storage, so a model downloaded once loads offline.

const webllmUrl = new URL('../lib/web-llm/web-llm.js', import.meta.url).href;
let webllm = null;
let engine = null;
let loadedModel = null;
const running = new Set();

async function lib() {
    webllm ??= await import(webllmUrl);
    return webllm;
}

/**
 * What the browser can do.
 * @returns {Promise<{webgpu: boolean, adapter: string, shaderF16: boolean, error?: string}>}
 */
export async function detect() {
    if (!('gpu' in navigator)) {
        return { webgpu: false, adapter: '', shaderF16: false, error: 'WebGPU is not available in this browser.' };
    }
    try {
        const adapter = await navigator.gpu.requestAdapter();
        if (!adapter) {
            return { webgpu: false, adapter: '', shaderF16: false, error: 'No WebGPU adapter (GPU blocked or not supported).' };
        }
        let name = '';
        try {
            const info = adapter.info ?? (adapter.requestAdapterInfo ? await adapter.requestAdapterInfo() : null);
            name = info ? [info.vendor, info.architecture, info.description].filter(Boolean).join(' ') : '';
        } catch {
            name = '';
        }
        return { webgpu: true, adapter: name, shaderF16: adapter.features.has('shader-f16') };
    } catch (e) {
        return { webgpu: false, adapter: '', shaderF16: false, error: String(e?.message ?? e) };
    }
}

/**
 * The models WebLLM ships, as id, VRAM need (MB) and whether they suit small GPUs.
 * @returns {Promise<Array<{id: string, vramMb: number, lowResource: boolean}>>}
 */
export async function listModels() {
    const { prebuiltAppConfig } = await lib();
    return prebuiltAppConfig.model_list
        .filter(m => !m.model_type || m.model_type === 0)
        .map(m => ({ id: m.model_id, vramMb: Math.round(m.vram_required_MB ?? 0), lowResource: !!m.low_resource_required }));
}

/** @param {string} modelId @returns {Promise<boolean>} Whether the model is already downloaded. */
export async function isCached(modelId) {
    try {
        const { hasModelInCache } = await lib();
        return await hasModelInCache(modelId);
    } catch {
        return false;
    }
}

/** @returns {string | null} The model loaded now. */
export function loaded() {
    return loadedModel;
}

/**
 * Loads a model (downloads it the first time), reporting progress to C# (OnLoadProgress).
 * @param {any} dotnet DotNetObjectReference of WebLlmEngine.
 * @param {string} modelId The model.
 */
export async function load(dotnet, modelId) {
    if (loadedModel === modelId && engine) {
        return;
    }
    const { CreateWebWorkerMLCEngine } = await lib();
    const progress = report => {
        dotnet.invokeMethodAsync('OnLoadProgress', report.progress ?? 0, report.text ?? '').catch(() => {});
    };
    if (engine) {
        await engine.reload(modelId);
    } else {
        const worker = new Worker(new URL('./webllm-worker.js', import.meta.url), { type: 'module' });
        engine = await CreateWebWorkerMLCEngine(worker, modelId, { initProgressCallback: progress });
    }
    loadedModel = modelId;
}

/**
 * Writes an answer, passing its pieces to C# (OnChunk, then OnEnd).
 * @param {any} dotnet DotNetObjectReference of WebLlmEngine.
 * @param {number} id Request id chosen by C#.
 * @param {string} requestJson The OpenAI-style request (messages and sampling parameters) as JSON.
 */
export async function chat(dotnet, id, requestJson) {
    if (!engine) {
        await dotnet.invokeMethodAsync('OnEnd', id, 'The local model is not loaded.', null);
        return;
    }
    running.add(id);
    let error = null;
    let usage = null;
    try {
        const request = JSON.parse(requestJson);
        const stream = await engine.chat.completions.create({ ...request, stream: true, stream_options: { include_usage: true } });
        for await (const chunk of stream) {
            if (!running.has(id)) {
                break;
            }
            const text = chunk.choices?.[0]?.delta?.content ?? '';
            if (text) {
                await dotnet.invokeMethodAsync('OnChunk', id, text);
            }
            if (chunk.usage) {
                usage = { prompt: chunk.usage.prompt_tokens ?? 0, completion: chunk.usage.completion_tokens ?? 0 };
            }
        }
    } catch (e) {
        error = String(e?.message ?? e);
    } finally {
        running.delete(id);
    }
    try {
        await dotnet.invokeMethodAsync('OnEnd', id, error, usage ? JSON.stringify(usage) : null);
    } catch {
        // The window is closed.
    }
}

/** Stops the answer being written. @param {number} id Request id. */
export async function stop(id) {
    running.delete(id);
    if (engine) {
        try {
            await engine.interruptGenerate();
        } catch {
            // Nothing running.
        }
    }
}

/** Frees the GPU memory. */
export async function unload() {
    if (engine) {
        try {
            await engine.unload();
        } catch {
            // Already gone.
        }
    }
    loadedModel = null;
}

/** Deletes a downloaded model from the browser's cache. @param {string} modelId The model. */
export async function remove(modelId) {
    const { deleteModelAllInfoInCache } = await lib();
    if (loadedModel === modelId) {
        await unload();
    }
    await deleteModelAllInfoInCache(modelId);
}

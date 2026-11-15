// Kazo IA: the browser's fetch for the AI services (OpenRouter, OpenAI, Ollama, LM Studio...).
//
// Why JavaScript: the Kazo OS SDK has no direct HTTP to the Internet without the optional Kazo
// server (KazoHttpClient goes through its relay), and the published OS trims System.Net.Http, so an
// installed app cannot count on HttpClient. This module only moves bytes: it starts a request,
// hands each decoded piece of the body to C# (BrowserStreamingHttp.OnChunk) and says when it ends.
// Everything else (JSON, Server-Sent Events, errors, the capability check) is C#.

const requests = new Map();

/**
 * Starts a request and waits for its status line.
 * @param {any} dotnet DotNetObjectReference of BrowserStreamingHttp.
 * @param {number} id Request id chosen by C#.
 * @param {string} method GET or POST.
 * @param {string} url Absolute address.
 * @param {Record<string, string>} headers Headers.
 * @param {string | null} body Body.
 * @returns {Promise<{status: number, contentType: string, error?: string}>}
 */
export async function start(dotnet, id, method, url, headers, body) {
    const controller = new AbortController();
    requests.set(id, controller);
    let response;
    try {
        response = await fetch(url, {
            method,
            headers,
            body: body ?? undefined,
            signal: controller.signal,
            credentials: 'omit',
            cache: 'no-store',
            mode: 'cors',
        });
    } catch (error) {
        requests.delete(id);
        return { status: 0, contentType: '', error: describe(error) };
    }

    // The body is read in the background; C# receives the pieces in order.
    pump(dotnet, id, response, controller);
    return { status: response.status, contentType: response.headers.get('content-type') ?? '' };
}

/**
 * Stops a request (the person pressed Stop, or the race is over).
 * @param {number} id Request id.
 */
export function abort(id) {
    const controller = requests.get(id);
    if (controller) {
        controller.abort();
        requests.delete(id);
    }
}

async function pump(dotnet, id, response, controller) {
    let error = null;
    try {
        if (response.body) {
            const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
            for (;;) {
                const { value, done } = await reader.read();
                if (done) {
                    break;
                }
                if (value) {
                    await dotnet.invokeMethodAsync('OnChunk', id, value);
                }
            }
        } else {
            const text = await response.text();
            if (text) {
                await dotnet.invokeMethodAsync('OnChunk', id, text);
            }
        }
    } catch (e) {
        error = controller.signal.aborted ? null : describe(e);
    } finally {
        requests.delete(id);
    }
    try {
        await dotnet.invokeMethodAsync('OnEnd', id, error);
    } catch {
        // The window is closed.
    }
}

function describe(error) {
    if (error && error.name === 'AbortError') {
        return 'aborted';
    }
    return (error && error.message) ? error.message : String(error);
}

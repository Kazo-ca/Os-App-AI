// Kazo AI: the Web Worker that runs the local model (WebLLM over WebGPU), so the window stays
// responsive while the model loads and writes. WebLLM (Apache-2.0) is vendored in ../lib/web-llm/.
import { WebWorkerMLCEngineHandler } from '../lib/web-llm/web-llm.js';

const handler = new WebWorkerMLCEngineHandler();
self.onmessage = message => handler.onmessage(message);

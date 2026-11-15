# Third-party notices

Kazo AI is MIT-licensed ([LICENSE](LICENSE)). It ships one third-party work, unmodified except for
the removal of its source-map comment:

## WebLLM 0.2.88

- File: `src/Kazo.Ai/wwwroot/lib/web-llm/web-llm.js` (the self-contained ES module of the npm
  package `@mlc-ai/web-llm`), used by `wwwroot/js/webllm-bridge.js` and `webllm-worker.js` to run
  the local model on WebGPU.
- Source: <https://github.com/mlc-ai/web-llm>, copyright the MLC LLM authors.
- License: Apache License 2.0, full text in
  [`src/Kazo.Ai/wwwroot/lib/web-llm/LICENSE`](src/Kazo.Ai/wwwroot/lib/web-llm/LICENSE).
- The bundle includes:
  - the TVM web runtime (tvmjs) of Apache TVM, Apache License 2.0;
  - the MLC web tokenizers (`@mlc-ai/web-tokenizers`, built from Hugging Face tokenizers and
    SentencePiece), Apache License 2.0;
  - loglevel by Tim Perry, MIT License: "Copyright (c) 2013 Tim Perry. Permission is hereby granted,
    free of charge, to any person obtaining a copy of this software and associated documentation
    files (the "Software"), to deal in the Software without restriction, including without
    limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is furnished to do so,
    subject to the following conditions: The above copyright notice and this permission notice
    shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS
    PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND."

The model weights are not part of the app: the browser downloads them from Hugging Face when the
person loads a local model, under each model's own license (shown on its Hugging Face page).

# Opt-in LLM integration test models

Drop a GGUF file here to enable the `LocalLlmPipelineIntegrationTests`. When no file is present, the tests silently pass (matching the repo's `TestVnasData` convention per CLAUDE.md — "test data files absent → silently skip").

## Expected filename

```
tests/Yaat.Client.Tests/TestData/llm/test-model.gguf
```

Symlink, hardlink, or copy — whatever works. The file is `.gitignore`d so it never gets committed.

## Recommended model

**Qwen2.5-1.5B-Instruct Q4_K_M** (~1 GB) — the sweet spot for accuracy vs speed on a consumer GPU. Apache 2.0 licensed.

Download from Hugging Face:

```
https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf
```

Then rename or symlink to `test-model.gguf`.

### Smaller alternatives (faster, less accurate)

- **Qwen2.5-0.5B-Instruct Q4_K_M** (~400 MB) — fastest, minimally usable for simple commands. Small models hallucinate on anything beyond the examples in the system prompt.
- **Llama-3.2-1B-Instruct Q4_K_M** (~800 MB) — similar ballpark to Qwen 1.5B, Meta's license.
- **TinyLlama-1.1B-Chat Q4_K_M** (~650 MB) — older, less accurate, but smallest acceptable size.

### Larger alternatives (slower, more accurate)

- **Qwen2.5-3B-Instruct Q4_K_M** (~2 GB) — best accuracy that still runs comfortably on an 8 GB GPU.
- **Phi-3-mini-4k-Instruct Q4_K_M** (~2.3 GB) — Microsoft's small instruct model, often the most instruction-faithful in this size class.

## Running the tests

With the file in place:

```bash
dotnet test tests/Yaat.Client.Tests/Yaat.Client.Tests.csproj \
    --filter "FullyQualifiedName~LocalLlmPipelineIntegration"
```

On a machine with CUDA available, inference runs on GPU automatically via `LlmCudaFixture` which calls `NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true)` before any weights load. On a CPU-only machine, the same tests run via CPU fallback — just slower.

Expected runtime per test case with Qwen2.5-1.5B Q4_K_M on a modern GPU: ~100–300 ms. Full 10-case suite: ~2–5 s.

## Why opt-in rather than bundled

- GGUF files are 400 MB – 2 GB each — too large to commit to git.
- CI runners don't have GPUs, so GPU-accelerated integration tests would either time out on CPU fallback or need a self-hosted CUDA runner.
- Different developers may prefer different models depending on their hardware. The test harness is model-agnostic as long as the output is plausibly "instruction-tuned canonical-command producer".

## What the tests verify

Each test feeds a spoken ATC transcript through the full `LocalLlmCommandMapper` → `LocalLlmService` → LLamaSharp pipeline and asserts:

1. `MapResult` is non-null (meaning `NormalizeOutput` validated the LLM's raw text as a canonical command),
2. The canonical command contains the expected literal (e.g. "5000" for "climb and maintain five thousand").

Assertions are deliberately tolerant — small models drift at inference time even at `Temperature=0.1`. The tests are meant to catch regressions in the prompt / sampling params / validator, not nail down exact model output.

A negative case ("good morning how are you") asserts `MapResult` is null, so chit-chat never leaks through as a command.

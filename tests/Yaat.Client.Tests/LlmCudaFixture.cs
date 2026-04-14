using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// xUnit fixture that sets up LM-Kit's licensing once and provides a shared
/// <see cref="LocalLlmService"/> backed by an opt-in test model. LM-Kit owns backend selection
/// internally — there is no NativeLibraryConfig dance to perform — so this fixture is much
/// simpler than its LLamaSharp predecessor.
///
/// The test model is identified by <see cref="ModelSource"/>, which can be a curated LM-Kit
/// model ID (e.g. <c>qwen3.5:4b</c>), an absolute file path to a local GGUF, or a URI.
/// On first use LM-Kit downloads / loads the model into its own cache; subsequent runs reuse it.
/// When the source is a file path that doesn't exist, the fixture leaves
/// <see cref="SharedServiceOrNull"/> null so tests that gate on it silently skip — matching the
/// repo's <c>TestVnasData</c> "absent test data → silent skip" convention per CLAUDE.md.
/// </summary>
public sealed class LlmCudaFixture : IDisposable
{
    /// <summary>
    /// LM-Kit model source for integration tests. Set the <c>LMKIT_TEST_MODEL</c> environment
    /// variable (e.g. <c>gemma4:e4b</c>) to opt in. Tests gate on <see cref="ModelAvailable"/>
    /// and silently skip when the env var is unset, so CI runners (which can't pay the
    /// multi-GB model download cost or run inference at acceptable speed) skip these tests
    /// without producing failures. Recommended local value: <c>gemma4:e4b</c> — validated
    /// 2026-04-14 against the LocalLlmPipelineIntegrationTests suite (12/12 pass on warm cache,
    /// individual inferences ~600 ms on a discrete GPU).
    /// </summary>
    public static string? ModelSource => Environment.GetEnvironmentVariable("LMKIT_TEST_MODEL");

    /// <summary>
    /// True when the LMKIT_TEST_MODEL env var points at something the runtime can load. For
    /// curated LM-Kit IDs this is true as soon as the env var is set (LM-Kit downloads on
    /// demand); for absolute file paths it also requires the file to exist on disk. CI runners
    /// without the env var see false here and the tests skip silently — matching the repo's
    /// <c>TestVnasData</c> "absent test data → silent skip" convention per CLAUDE.md.
    /// </summary>
    public static bool ModelAvailable
    {
        get
        {
            var src = ModelSource;
            if (string.IsNullOrWhiteSpace(src))
            {
                return false;
            }
            if (Path.IsPathRooted(src))
            {
                return File.Exists(src);
            }
            return true;
        }
    }

    private readonly LocalLlmService? _sharedService;

    public LlmCudaFixture()
    {
        // Funnel through the solution-wide helper so the license key is resolved from the same
        // place as the production client (LMKIT_LICENSE_KEY env var or solution-root .env file)
        // and falls back to Community Edition when neither is set. Safe to call multiple times —
        // the helper captures "already initialized" exceptions internally.
        LmKitLicense.Initialize();

        if (ModelAvailable)
        {
            // ModelAvailable guarantees ModelSource is non-null and either a curated ID or an
            // existing file path — the null-forgiving operator is safe here.
            var config = new FixtureLlmConfig(ModelSource!, gpuLayers: -1);
            _sharedService = new LocalLlmService(config);
        }
    }

    /// <summary>Shared <see cref="LocalLlmService"/> — null when no model source is configured.</summary>
    public LocalLlmService? SharedServiceOrNull => _sharedService;

    public void Dispose()
    {
        _sharedService?.Dispose();
    }

    private sealed class FixtureLlmConfig : ILlmRuntimeConfig
    {
        public FixtureLlmConfig(string modelPath, int gpuLayers)
        {
            ModelPath = modelPath;
            GpuLayers = gpuLayers;
        }

        public string ModelPath { get; }
        public int GpuLayers { get; }
    }
}

[CollectionDefinition("LLM")]
public sealed class LlmCollection : ICollectionFixture<LlmCudaFixture>
{
    // xUnit marker class — no code, just the type declaration.
}

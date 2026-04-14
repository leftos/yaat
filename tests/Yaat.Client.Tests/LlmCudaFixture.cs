using LMKit.Licensing;
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
    /// Override via the LMKIT_TEST_MODEL environment variable to point integration tests at a
    /// different model without code changes. Default is <c>gemma4:e4b</c> from LM-Kit's curated
    /// catalog — validated against the LocalLlmPipelineIntegrationTests suite on 2026-04-14.
    /// Gemma 4 E4B handles "two three zero" → 230 correctly where qwen3.5:4b over-extends to 2300,
    /// at the cost of a one-time ~6 GB download (subsequent runs reuse LM-Kit's cache and
    /// individual test inferences run in ~600 ms — same speed as qwen3.5:4b).
    /// </summary>
    public static string ModelSource => Environment.GetEnvironmentVariable("LMKIT_TEST_MODEL") ?? "gemma4:e4b";

    /// <summary>
    /// True when the configured model source can be loaded. For curated LM-Kit IDs this is
    /// always true (LM-Kit downloads on demand); for absolute file paths it requires the file
    /// to exist on disk.
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
        // Empty key signals LM-Kit Community Edition. Safe to call multiple times — LM-Kit
        // initializes its licensing layer once per process.
        try
        {
            LicenseManager.SetLicenseKey("");
        }
        catch
        {
            // Already initialized (another fixture or test host) — swallow.
        }

        if (ModelAvailable)
        {
            var config = new FixtureLlmConfig(ModelSource, gpuLayers: -1);
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

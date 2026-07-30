using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Guards against the shipped speech-model defaults drifting from the validated recommendations
/// in <see cref="LmKitModelCatalog"/>. A user who enables speech without ever opening the model
/// pickers runs whatever <see cref="UserPreferences"/> defaults to — that default must be the
/// model the pipeline was actually validated against (see the grammar-calibration notes in
/// <c>CanonicalCommandGrammar</c>: qwen3.5:4b emitted end-of-generation for every input under
/// the canonical-command grammar, so shipping it as the default silently killed the LLM fallback).
/// </summary>
public class SpeechModelDefaultsTests
{
    [Fact]
    public void DefaultLlmModel_MatchesCatalogRecommendation()
    {
        Assert.Equal(LmKitModelCatalog.RecommendedLlmId, new UserPreferences().LlmModelPath);
    }

    [Fact]
    public void DefaultWhisperModel_MatchesCatalogRecommendation()
    {
        Assert.Equal(LmKitModelCatalog.RecommendedWhisperId, new UserPreferences().WhisperModelSize);
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SherpaOnnx;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The build overlays our patched sherpa-onnx CPU native over NuGet's (build/sherpa/SherpaNativeOverlay.targets).
/// These check the overlaid files beside the test assembly are the ones our manifest lists, and that they carry
/// the n-best output and still run Piper.
/// </summary>
public sealed partial class SherpaNativeOverlayTests
{
    [Fact]
    public void Overlaid_Native_Matches_Its_Manifest()
    {
        string? rid = HostRid();
        Assert.SkipUnless(rid is not null, $"No overlaid sherpa native for {RuntimeInformation.RuntimeIdentifier}.");

        string nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", rid!, "native");
        string manifestPath = Path.Combine(nativeDir, "sherpa-native-MANIFEST.txt");
        string fallbackPath = Path.Combine(nativeDir, "sherpa-native-FALLBACK.txt");
        if (File.Exists(fallbackPath))
        {
            Assert.Skip($"The build fell back to NuGet's stock sherpa native: {File.ReadAllText(fallbackPath).Trim()}");
        }

        Assert.True(File.Exists(manifestPath), $"{manifestPath} is missing: the sherpa native overlay did not run.");

        string[] lines = File.ReadAllLines(manifestPath);
        Dictionary<string, string> hashes = [];
        string? head = null;
        foreach (string line in lines)
        {
            Match hashLine = HashLine().Match(line);
            if (hashLine.Success)
            {
                hashes[hashLine.Groups[2].Value] = hashLine.Groups[1].Value;
            }
            else if (line.StartsWith("HEAD: ", StringComparison.Ordinal))
            {
                head = line["HEAD: ".Length..].Trim();
            }
        }

        (string cApi, string onnxRuntime) = LibraryNames(rid!);
        Assert.Equal(hashes[cApi], Sha256Of(Path.Combine(nativeDir, cApi)));
        Assert.Equal(hashes[onnxRuntime], Sha256Of(Path.Combine(nativeDir, onnxRuntime)));

        using var downloads = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json")));
        Assert.Equal(downloads.RootElement.GetProperty("natives").GetProperty("commit").GetString(), head);
    }

    [Fact]
    public void Overlaid_Native_Carries_Nbest()
    {
        string? dir = Environment.GetEnvironmentVariable("YAAT_TEST_RASR_INT8_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(dir) && Directory.Exists(dir), "YAAT_TEST_RASR_INT8_DIR names no rasr int8 model directory.");

        using var engine = new SherpaSttEngine(dir!, "cpu", new SherpaDecoding(SherpaDecodingMethod.Beam, 4, 1.5f), SherpaOfflineNativeApi.Instance);

        Assert.Null(engine.NativeProblem());
    }

    [Fact]
    public void Overlaid_Native_Runs_Piper()
    {
        string? dir = Environment.GetEnvironmentVariable("YAAT_TEST_PIPER_VOICE_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(dir) && PilotVoicePack.IsComplete(dir), "YAAT_TEST_PIPER_VOICE_DIR names no complete Piper voice.");

        using OfflineTts tts = SherpaOnnxPilotVoiceSynthesizer.CreateOfflineTts(
            Path.Combine(dir!, PilotVoicePack.ModelFileName),
            Path.Combine(dir!, PilotVoicePack.TokensFileName)
        );
        // espeak-ng is process-global: phonemize from the same data directory EspeakPhonemizerTests uses (the
        // package's copy beside the test assembly, identical to the voice pack's) so the two classes can share a run.
        var gen = new OfflineTtsGenerationConfig
        {
            Sid = 0,
            Speed = 1.0f,
            PhonemeCodepoints = EspeakPhonemizer.Phonemize(
                "cleared to land",
                EspeakPhonemizer.VoiceFor(tts.Lang),
                Path.Combine(AppContext.BaseDirectory, "espeak-ng-data")
            ),
        };
        OfflineTtsGeneratedAudio audio = tts.GenerateWithConfig("", gen, null);

        Assert.NotEmpty(audio.Samples);
        Assert.True(audio.SampleRate > 0, $"SampleRate was {audio.SampleRate}.");
    }

    private static string? HostRid()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (arch == Architecture.X64) ? "win-x64" : null;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return (arch == Architecture.X64) ? "linux-x64" : null;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return arch switch
            {
                Architecture.Arm64 => "osx-arm64",
                Architecture.X64 => "osx-x64",
                _ => null,
            };
        }
        return null;
    }

    private static (string CApi, string OnnxRuntime) LibraryNames(string rid)
    {
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            return ("sherpa-onnx-c-api.dll", "onnxruntime.dll");
        }
        string extension = rid.StartsWith("linux-", StringComparison.Ordinal) ? ".so" : ".dylib";
        return ($"libsherpa-onnx-c-api{extension}", $"libonnxruntime{extension}");
    }

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [GeneratedRegex(@"^([0-9a-f]{64})\s+(\S+)$")]
    private static partial Regex HashLine();
}

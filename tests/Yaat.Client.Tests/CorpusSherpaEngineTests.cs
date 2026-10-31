using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>What a sherpa model directory must hold for each decoding, checked before any native code loads.</summary>
public sealed class CorpusSherpaEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-sherpa-engine-").FullName;

    public CorpusSherpaEngineTests()
    {
        File.WriteAllText(Path.Combine(_dir, "encoder.onnx"), string.Empty);
        File.WriteAllText(Path.Combine(_dir, "tokens.txt"), string.Empty);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Beam_Needs_A_Bpe_Vocab()
    {
        using var engine = new SherpaCorpusSttEngine(_dir, "cpu", new SherpaDecoding(SherpaDecodingMethod.Beam, 4, 1.5f));

        Assert.Equal($"sherpa model dir {_dir} has no bpe.vocab, which --decoding beam needs", engine.SetupError());
    }

    [Fact]
    public void Greedy_Needs_No_Bpe_Vocab()
    {
        using var engine = new SherpaCorpusSttEngine(_dir, "cpu", SherpaDecoding.Greedy);

        Assert.Null(engine.SetupError());
    }
}

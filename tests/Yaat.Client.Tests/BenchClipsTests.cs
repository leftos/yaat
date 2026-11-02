using System.Text.Json;
using Xunit;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>The committed bench clip set the in-app speech benchmark transcribes.</summary>
public sealed class BenchClipsTests
{
    /// <summary>The size budget of the committed WAVs: 1.7 MB.</summary>
    private const long MaxTotalBytes = 1_782_579;

    private static readonly string ClipsDirectory = Path.Combine(AppContext.BaseDirectory, "Speech", "BenchClips");

    [Fact]
    public void The_Set_Has_Exactly_12_Clips()
    {
        IReadOnlyList<BenchClip> clips = BenchClips.Load(ClipsDirectory);

        Assert.Equal(12, clips.Count);
        Assert.All(clips, c => Assert.NotEmpty(c.Samples));
    }

    [Fact]
    public void Every_Listed_Clip_Has_Its_WAV_And_Every_WAV_Is_Listed()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClipsDirectory, BenchClips.ManifestFileName)));
        string[] listed = [.. manifest.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).Order(StringComparer.Ordinal)];
        string[] wavs =
        [
            .. Directory.GetFiles(ClipsDirectory, "*.wav").Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal),
        ];

        Assert.Equal(listed, wavs);
    }

    [Fact]
    public void The_WAVs_Fit_The_Size_Budget()
    {
        long total = Directory.GetFiles(ClipsDirectory, "*.wav").Sum(p => new FileInfo(p).Length);

        Assert.InRange(total, 1, MaxTotalBytes);
    }

    [Fact]
    public void Every_Expected_Text_Is_Non_Empty()
    {
        IReadOnlyList<BenchClip> clips = BenchClips.Load(ClipsDirectory);

        Assert.All(clips, c => Assert.False(string.IsNullOrWhiteSpace(c.Expected), c.Id));
    }
}

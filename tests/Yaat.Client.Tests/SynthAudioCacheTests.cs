using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The content-addressed synth-audio cache: a key over every synthesis input, a lossless float
/// round trip, first-writer-wins storage, and unreadable entries reported as a miss — deleted only
/// when the format is corrupt — rather than crashing a run.
/// </summary>
public sealed class SynthAudioCacheTests : IDisposable
{
    private const string BaseVoice = "vits-piper-en_US-libritts_r-medium:0123456789abcdef";
    private const string BaseText = "turn left heading three six zero";
    private const int BaseSpeaker = 7;
    private const float BaseSpeed = 1.0f;
    private const float BaseLengthScale = 1.0f;
    private const int BaseSampleRate = 16000;
    private const int BaseLeadingMs = 400;
    private const int BaseTrailingMs = 400;

    /// <summary>
    /// The key <see cref="BaseKey"/> must produce. Pinning it to a literal catches any change to the
    /// field order, separator or number formatting that would silently re-key every cached entry.
    /// </summary>
    private const string BaseKeyHex = "094cb0d8fd3d2f60e2a73a720851c2f242f8f2b1c64efb8075380e9883910a13";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-synth-cache-" + Guid.NewGuid().ToString("N"));
    private readonly SynthAudioCache _cache;

    public SynthAudioCacheTests() => _cache = new SynthAudioCache(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SynthAudioKeyInputs Inputs() =>
        new()
        {
            VoiceIdentity = BaseVoice,
            Text = BaseText,
            SpeakerId = BaseSpeaker,
            Speed = BaseSpeed,
            LengthScale = BaseLengthScale,
            SampleRate = BaseSampleRate,
            LeadingSilenceMs = BaseLeadingMs,
            TrailingSilenceMs = BaseTrailingMs,
        };

    private static string BaseKey() => SynthAudioCache.ComputeKey(Inputs());

    private static float[] Samples() => [-2.0f, -1.0f, -0.3333f, 0.0f, 0.123456789f, 0.5f, 1.0f, 2.0f];

    private string PathFor(string key) => Path.Combine(_root, key + ".wav");

    [Fact]
    public void Base_Key_Is_Pinned_To_A_Known_Hex() => Assert.Equal(BaseKeyHex, BaseKey());

    [Fact]
    public void Key_Changes_With_Text() =>
        Assert.NotEqual(BaseKey(), SynthAudioCache.ComputeKey(Inputs() with { Text = "turn right heading three six zero" }));

    [Fact]
    public void Key_Changes_With_Speaker() => Assert.NotEqual(BaseKey(), SynthAudioCache.ComputeKey(Inputs() with { SpeakerId = BaseSpeaker + 1 }));

    [Fact]
    public void Key_Changes_With_Speed() => Assert.NotEqual(BaseKey(), SynthAudioCache.ComputeKey(Inputs() with { Speed = 1.04f }));

    [Fact]
    public void Key_Changes_With_Length_Scale() => Assert.NotEqual(BaseKey(), SynthAudioCache.ComputeKey(Inputs() with { LengthScale = 1.25f }));

    [Fact]
    public void Key_Changes_With_Sample_Rate() => Assert.NotEqual(BaseKey(), SynthAudioCache.ComputeKey(Inputs() with { SampleRate = 22050 }));

    [Fact]
    public void Key_Changes_With_Leading_Silence() =>
        Assert.NotEqual(BaseKey(), SynthAudioCache.ComputeKey(Inputs() with { LeadingSilenceMs = BaseLeadingMs + 1 }));

    [Fact]
    public void Key_Changes_With_Trailing_Silence() =>
        Assert.NotEqual(BaseKey(), SynthAudioCache.ComputeKey(Inputs() with { TrailingSilenceMs = BaseTrailingMs + 1 }));

    [Fact]
    public void Key_Changes_With_Voice_Identity() =>
        Assert.NotEqual(
            BaseKey(),
            SynthAudioCache.ComputeKey(Inputs() with { VoiceIdentity = "vits-piper-en_US-libritts_r-medium:ffffffffffffffff" })
        );

    [Fact]
    public void Key_Does_Not_Collide_When_Field_Values_Are_Re_Split()
    {
        string left = SynthAudioCache.ComputeKey(Inputs() with { VoiceIdentity = "v", Text = "ab" });
        string right = SynthAudioCache.ComputeKey(Inputs() with { VoiceIdentity = "va", Text = "b" });
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Put_Then_TryGet_Round_Trips_Samples()
    {
        float[] samples = Samples();
        _cache.PutOrGetExisting(BaseKey(), samples, BaseSampleRate);

        Assert.True(_cache.TryGet(BaseKey(), out float[] restored));
        Assert.Equal(samples, restored);
    }

    [Fact]
    public void Missing_Key_Is_A_Miss()
    {
        Assert.False(_cache.TryGet(BaseKey(), out float[] samples));
        Assert.Empty(samples);
    }

    [Fact]
    public void Put_Leaves_No_Temp_File()
    {
        _cache.PutOrGetExisting(BaseKey(), Samples(), BaseSampleRate);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Second_Put_Keeps_And_Returns_The_First_Samples()
    {
        float[] first = [0.25f, -0.5f];
        float[] second = [0.75f, 0.125f];

        float[] stored = _cache.PutOrGetExisting(BaseKey(), first, BaseSampleRate);
        float[] winner = _cache.PutOrGetExisting(BaseKey(), second, BaseSampleRate);

        Assert.Equal(first, stored);
        Assert.Equal(first, winner);
        Assert.True(_cache.TryGet(BaseKey(), out float[] onDisk));
        Assert.Equal(first, onDisk);
    }

    [Fact]
    public void Truncated_Entry_Is_A_Miss_Deleted_And_Logged()
    {
        string key = BaseKey();
        _cache.PutOrGetExisting(key, Samples(), BaseSampleRate);
        string path = PathFor(key);
        byte[] full = File.ReadAllBytes(path);
        File.WriteAllBytes(path, full[..(full.Length - 8)]);

        using var capture = new CapturingLoggerFactory();
        SimLog.InitializeForTest(capture);

        Assert.False(_cache.TryGet(key, out float[] samples));
        Assert.Empty(samples);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(capture.Warnings);
    }

    [Fact]
    public void Corrupt_Entry_Is_A_Miss_Deleted_And_Logged()
    {
        Directory.CreateDirectory(_root);
        string key = BaseKey();
        string path = PathFor(key);
        File.WriteAllText(path, "not a wav file");

        using var capture = new CapturingLoggerFactory();
        SimLog.InitializeForTest(capture);

        Assert.False(_cache.TryGet(key, out float[] samples));
        Assert.Empty(samples);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(capture.Warnings);
    }

    [Fact]
    public void Entry_With_An_Oversized_Fmt_Chunk_Is_A_Miss_Deleted_And_Logged()
    {
        Directory.CreateDirectory(_root);
        string key = BaseKey();
        string path = PathFor(key);
        byte[] bytes = new byte[64];
        "RIFF"u8.CopyTo(bytes);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        int oversized = 0x7FFFFFF0;
        bytes[16] = (byte)oversized;
        bytes[17] = (byte)(oversized >> 8);
        bytes[18] = (byte)(oversized >> 16);
        bytes[19] = (byte)(oversized >> 24);
        File.WriteAllBytes(path, bytes);

        using var capture = new CapturingLoggerFactory();
        SimLog.InitializeForTest(capture);

        Assert.False(_cache.TryGet(key, out float[] samples));
        Assert.Empty(samples);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(capture.Warnings);
    }

    [Fact]
    public void PutOrGetExisting_With_A_Locked_Existing_Entry_Returns_The_New_Samples()
    {
        string key = BaseKey();
        float[] first = [0.25f, -0.5f];
        float[] second = [0.75f, 0.125f];
        _cache.PutOrGetExisting(key, first, BaseSampleRate);
        string path = PathFor(key);

        using var capture = new CapturingLoggerFactory();
        SimLog.InitializeForTest(capture);

        float[] returned;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(held.CanRead);
            returned = _cache.PutOrGetExisting(key, second, BaseSampleRate);
        }

        Assert.Equal(second, returned);
        Assert.True(File.Exists(path));
        Assert.NotEmpty(capture.Warnings);
    }

    [Fact]
    public void PutOrGetExisting_With_A_Corrupt_Existing_Entry_Returns_The_New_Samples_And_Deletes_It()
    {
        Directory.CreateDirectory(_root);
        string key = BaseKey();
        string path = PathFor(key);
        File.WriteAllText(path, "not a wav file");
        float[] samples = [0.25f, -0.5f];

        using var capture = new CapturingLoggerFactory();
        SimLog.InitializeForTest(capture);

        float[] returned = _cache.PutOrGetExisting(key, samples, BaseSampleRate);

        Assert.Equal(samples, returned);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(capture.Warnings);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Warnings);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                if (logLevel >= LogLevel.Warning)
                {
                    sink.Add(formatter(state, exception));
                }
            }
        }
    }
}

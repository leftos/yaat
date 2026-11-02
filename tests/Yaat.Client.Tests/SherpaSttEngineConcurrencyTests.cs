using SherpaOnnx;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>What a <see cref="SherpaSttEngine"/> promises when a prewarm, a push-to-talk transcribe and a Dispose overlap.</summary>
public sealed class SherpaSttEngineConcurrencyTests
{
    [Fact]
    public async Task Concurrent_Transcribes_Create_One_Recognizer()
    {
        using FakeSherpaApi api = new();
        string dir = CreateModelDirectory();
        try
        {
            using SherpaSttEngine engine = new(
                dir,
                "cpu",
                SherpaDecoding.Greedy,
                api,
                new SherpaGpuFallbackProbes(() => -1, NativeStderrCapture.Current)
            );
            api.ArmCreate();
            Task<IReadOnlyList<SttHypothesis>> first = Task.Run(() => engine.Transcribe(Silence(), AudioCaptureService.SampleRate, string.Empty));
            Task<IReadOnlyList<SttHypothesis>> second = Task.Run(() => engine.Transcribe(Silence(), AudioCaptureService.SampleRate, string.Empty));

            WaitFor(api.CreateEntered, "CreateRecognizer");
            api.ReleaseCreate();

            IReadOnlyList<SttHypothesis>[] results = await Task.WhenAll(first, second);
            Assert.Equal(1, api.CreateRecognizerCalls);
            Assert.NotEmpty(results[0]);
            Assert.NotEmpty(results[1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_During_Decode_Never_Destroys_Mid_Decode()
    {
        using FakeSherpaApi api = new();
        string dir = CreateModelDirectory();
        try
        {
            SherpaSttEngine engine = new(dir, "cpu", SherpaDecoding.Greedy, api, new SherpaGpuFallbackProbes(() => -1, NativeStderrCapture.Current));
            api.ArmDecode();
            Task<IReadOnlyList<SttHypothesis>> transcribe = Task.Run(() =>
                engine.Transcribe(Silence(), AudioCaptureService.SampleRate, string.Empty)
            );

            WaitFor(api.DecodeEntered, "Decode");
            using ManualResetEventSlim disposeStarted = new();
            var dispose = Task.Run(
                () =>
                {
                    disposeStarted.Set();
                    engine.Dispose();
                },
                TestContext.Current.CancellationToken
            );
            WaitFor(disposeStarted, "Dispose");
            api.ReleaseDecode();

            await Task.WhenAll(transcribe, dispose);
            IReadOnlyList<SttHypothesis> entries = await transcribe;
            Assert.Equal(1, api.DestroyRecognizerCalls);
            Assert.Equal(0, api.DestroysDuringDecode);
            Assert.NotEmpty(entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Calls_After_Dispose_Make_No_Native_Call()
    {
        using FakeSherpaApi api = new();
        string dir = CreateModelDirectory();
        try
        {
            SherpaSttEngine engine = new(dir, "cpu", SherpaDecoding.Greedy, api, new SherpaGpuFallbackProbes(() => -1, NativeStderrCapture.Current));
            engine.Transcribe(Silence(), AudioCaptureService.SampleRate, string.Empty);
            engine.Dispose();
            int before = api.TotalCalls;

            Assert.Empty(engine.Transcribe(Silence(), AudioCaptureService.SampleRate, string.Empty));
            engine.Dispose();
            Assert.Equal(before, api.TotalCalls);

            using FakeSherpaApi beamApi = new();
            SherpaSttEngine beam = new(
                dir,
                "cpu",
                new SherpaDecoding(SherpaDecodingMethod.Beam, 4, 1.5f),
                beamApi,
                new SherpaGpuFallbackProbes(() => -1, NativeStderrCapture.Current)
            );
            beam.Dispose();
            string? problem = beam.NativeProblem();
            Assert.NotNull(problem);
            Assert.Contains("disposed", problem);
            Assert.Equal(0, beamApi.TotalCalls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static float[] Silence() => new float[AudioCaptureService.SampleRate / 2];

    private static string CreateModelDirectory()
    {
        string dir = Directory.CreateTempSubdirectory("sherpa-engine-concurrency-").FullName;
        string[] names = ["encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"];
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(dir, name), string.Empty);
        }
        return dir;
    }

    /// <summary>Waits on an event the engine leaves; the deadline is a hang guard a passing run never reaches.</summary>
    private static void WaitFor(ManualResetEventSlim entered, string what) =>
        Assert.True(entered.Wait(TimeSpan.FromMinutes(2)), $"{what} was never entered");

    /// <summary>Stands in for the native, counting calls and holding the ones a test arms.</summary>
    internal sealed class FakeSherpaApi : ISherpaOfflineApi, IDisposable
    {
        private const string Json = """{"text":"x","tokens":[],"ys_log_probs":[]}""";

        private readonly ManualResetEventSlim _createGate = new(initialState: true);
        private readonly ManualResetEventSlim _decodeGate = new(initialState: true);
        private int _createCalls;
        private int _destroyCalls;
        private int _createStreamCalls;
        private int _setOptionCalls;
        private int _acceptCalls;
        private int _decodeCalls;
        private int _resultCalls;
        private int _destroyStreamCalls;
        private int _decodeInProgress;
        private int _destroysDuringDecode;
        private int _nextHandle;

        public ManualResetEventSlim CreateEntered { get; } = new();

        public ManualResetEventSlim DecodeEntered { get; } = new();

        public int CreateRecognizerCalls => Volatile.Read(ref _createCalls);

        public int DestroyRecognizerCalls => Volatile.Read(ref _destroyCalls);

        public int DestroysDuringDecode => Volatile.Read(ref _destroysDuringDecode);

        public int TotalCalls =>
            CreateRecognizerCalls
            + DestroyRecognizerCalls
            + Volatile.Read(ref _createStreamCalls)
            + Volatile.Read(ref _setOptionCalls)
            + Volatile.Read(ref _acceptCalls)
            + Volatile.Read(ref _decodeCalls)
            + Volatile.Read(ref _resultCalls)
            + Volatile.Read(ref _destroyStreamCalls);

        public void ArmCreate() => _createGate.Reset();

        public void ReleaseCreate() => _createGate.Set();

        public void ArmDecode() => _decodeGate.Reset();

        public void ReleaseDecode() => _decodeGate.Set();

        public IntPtr CreateRecognizer(ref OfflineRecognizerConfig config)
        {
            Interlocked.Increment(ref _createCalls);
            CreateEntered.Set();
            _createGate.Wait();
            return NextHandle();
        }

        public void DestroyRecognizer(IntPtr recognizer)
        {
            Interlocked.Increment(ref _destroyCalls);
            if (Volatile.Read(ref _decodeInProgress) > 0)
            {
                Interlocked.Increment(ref _destroysDuringDecode);
            }
        }

        public IntPtr CreateStream(IntPtr recognizer, string hotwords)
        {
            Interlocked.Increment(ref _createStreamCalls);
            return NextHandle();
        }

        public void SetStreamOption(IntPtr stream, string key, string value) => Interlocked.Increment(ref _setOptionCalls);

        public void AcceptWaveform(IntPtr stream, int sampleRate, float[] samples) => Interlocked.Increment(ref _acceptCalls);

        public void Decode(IntPtr recognizer, IntPtr stream)
        {
            Interlocked.Increment(ref _decodeCalls);
            Interlocked.Increment(ref _decodeInProgress);
            DecodeEntered.Set();
            try
            {
                _decodeGate.Wait();
            }
            finally
            {
                Interlocked.Decrement(ref _decodeInProgress);
            }
        }

        public string? ResultJson(IntPtr stream)
        {
            Interlocked.Increment(ref _resultCalls);
            return Json;
        }

        public void DestroyStream(IntPtr stream) => Interlocked.Increment(ref _destroyStreamCalls);

        public void Dispose()
        {
            _createGate.Dispose();
            _decodeGate.Dispose();
            CreateEntered.Dispose();
            DecodeEntered.Dispose();
        }

        private IntPtr NextHandle() => new(Interlocked.Increment(ref _nextHandle));
    }
}

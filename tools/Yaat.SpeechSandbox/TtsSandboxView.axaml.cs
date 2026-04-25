using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NAudio.Dsp;
using NAudio.Wave;
using PortAudioSharp;
using SherpaOnnx;

namespace Yaat.SpeechSandbox;

/// <summary>
/// TTS sandbox tab — a durable home for the Piper VITS + sherpa-onnx + radio-FX experiments
/// that began in Yaat.Scratch as the M10.0 spike. Lets us iterate on speaker IDs, band-pass /
/// drive / squelch parameters, and pre-warm timing before locking the M10.3 implementation in
/// Yaat.Sim.Pilot / Yaat.Client.Audio.
///
/// Voice pack expected at <see cref="DefaultVoiceDirCandidates"/> — a Piper-format directory
/// with the .onnx model, tokens.txt, and an espeak-ng-data/ subdir. Auto-detected by walking up
/// from the .exe location looking for .tmp/voices/vits-piper-en_US-libritts_r-medium.
///
/// Audio playback uses PortAudioSharp2 (already a Yaat.Client dependency for STT capture). The
/// TTS model runs on CPU; sherpa-onnx 1.12.40 + Piper LibriTTS-R medium gives ~150-200 ms warm
/// synth latency for ATC-length utterances on commodity hardware.
/// </summary>
public partial class TtsSandboxView : UserControl
{
    private readonly StringBuilder _log = new();
    private OfflineTts? _tts;
    private string? _loadedVoiceDir;
    private bool _isLoading;

    // Owned PortAudio state — lifetime tied to first playback. Terminated in OnDetachedFromVisualTree.
    private bool _portAudioInitialized;
    private PortAudioSharp.Stream? _activeStream;
    private CancellationTokenSource? _activeStreamCts;

    private string? _lastWavPath;
    private float[]? _lastSamples;
    private int _lastSampleRate;

    public TtsSandboxView()
    {
        AvaloniaXamlLoader.Load(this);
        WireControls();

        var voiceBox = this.FindControl<TextBox>("VoiceDirBox")!;
        voiceBox.Text = ResolveDefaultVoiceDir() ?? "";
    }

    private void WireControls()
    {
        var loadBtn = this.FindControl<Button>("LoadModelButton")!;
        loadBtn.Click += (_, _) => _ = LoadModelAsync();

        var prewarmBtn = this.FindControl<Button>("PrewarmButton")!;
        prewarmBtn.Click += (_, _) => _ = PrewarmAsync();

        var synthBtn = this.FindControl<Button>("SynthAndPlayButton")!;
        synthBtn.Click += (_, _) => _ = SynthAndPlayAsync();

        var saveBtn = this.FindControl<Button>("SaveWavButton")!;
        saveBtn.Click += (_, _) => _ = SaveLastWavAsync();

        var exampleBtn = this.FindControl<Button>("RunExampleSetButton")!;
        exampleBtn.Click += (_, _) => _ = RunExampleSetAsync();

        var loadExampleBtn = this.FindControl<Button>("LoadExampleTextButton")!;
        loadExampleBtn.Click += (_, _) =>
            this.FindControl<TextBox>("TextBox")!.Text = "Cleared ILS twenty eight right approach, American twelve thirty four.";

        var stopBtn = this.FindControl<Button>("StopAudioButton")!;
        stopBtn.Click += (_, _) => StopActiveStream();

        // Slider value mirroring (so the read-out next to the slider always reflects the value).
        BindSliderText("SpeakerIdSlider", "SpeakerIdValueText", v => ((int)v).ToString(CultureInfo.InvariantCulture));
        BindSliderText("SpeedSlider", "SpeedValueText", v => v.ToString("F2", CultureInfo.InvariantCulture));
        BindSliderText("BpCenterSlider", "BpCenterValueText", v => ((int)v).ToString(CultureInfo.InvariantCulture));
        BindSliderText("BpQSlider", "BpQValueText", v => v.ToString("F2", CultureInfo.InvariantCulture));
        BindSliderText("DriveSlider", "DriveValueText", v => v.ToString("F2", CultureInfo.InvariantCulture));
        BindSliderText("SquelchSlider", "SquelchValueText", v => ((int)v).ToString(CultureInfo.InvariantCulture));
    }

    private void BindSliderText(string sliderName, string textName, Func<double, string> format)
    {
        var slider = this.FindControl<Slider>(sliderName)!;
        var text = this.FindControl<TextBlock>(textName)!;
        text.Text = format(slider.Value);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                text.Text = format(slider.Value);
            }
        };
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for
    /// <c>.tmp/voices/vits-piper-en_US-libritts_r-medium</c>. Mirrors the LmKitLicense .env walk.
    /// </summary>
    private static string? ResolveDefaultVoiceDir()
    {
        const string Relative = ".tmp/voices/vits-piper-en_US-libritts_r-medium";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Relative);
            if (File.Exists(Path.Combine(candidate, "en_US-libritts_r-medium.onnx")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private async Task LoadModelAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;

        var voiceDir = (this.FindControl<TextBox>("VoiceDirBox")!.Text ?? "").Trim();
        if (string.IsNullOrEmpty(voiceDir))
        {
            SetStatus("Voice pack directory is empty — paste a path or auto-detect.");
            _isLoading = false;
            return;
        }

        // Locate model + tokens. Piper voices ship the ONNX file with the same stem as the dir name.
        var dirInfo = new DirectoryInfo(voiceDir);
        var onnxFile = dirInfo.Exists ? dirInfo.GetFiles("*.onnx").FirstOrDefault() : null;

        if (onnxFile is null)
        {
            SetStatus($"No .onnx file found under {voiceDir}");
            _isLoading = false;
            return;
        }

        var tokensPath = Path.Combine(voiceDir, "tokens.txt");
        var dataDir = Path.Combine(voiceDir, "espeak-ng-data");

        if (!File.Exists(tokensPath))
        {
            SetStatus($"tokens.txt missing in {voiceDir}");
            _isLoading = false;
            return;
        }

        SetStatus("Loading model...");
        AppendLog($"Loading model: {onnxFile.FullName}");

        try
        {
            await Task.Run(() =>
            {
                _tts?.Dispose();
                var config = new OfflineTtsConfig();
                config.Model.Vits.Model = onnxFile.FullName;
                config.Model.Vits.Tokens = tokensPath;
                config.Model.Vits.DataDir = dataDir;
                config.Model.Vits.LengthScale = 1.0f;
                config.Model.NumThreads = 2;
                config.Model.Provider = "cpu";
                config.Model.Debug = 0;

                var loadSw = Stopwatch.StartNew();
                _tts = new OfflineTts(config);
                loadSw.Stop();

                Dispatcher.UIThread.Post(() =>
                {
                    _loadedVoiceDir = voiceDir;
                    AppendLog($"Loaded in {loadSw.ElapsedMilliseconds} ms — sample rate {_tts.SampleRate} Hz");
                    SetStatus($"Model ready ({loadSw.ElapsedMilliseconds} ms load).");
                    UpdateModelStatus();
                });
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Load failed: {ex.Message}");
            AppendLog(ex.ToString());
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void UpdateModelStatus()
    {
        var statusText = this.FindControl<TextBlock>("ModelStatusText")!;
        if (_tts is null)
        {
            statusText.Text = "No model loaded.";
            return;
        }

        statusText.Text = $"Loaded: {Path.GetFileName(_loadedVoiceDir)} | sample rate {_tts.SampleRate} Hz";
        this.FindControl<Button>("PrewarmButton")!.IsEnabled = true;
        this.FindControl<Button>("SynthAndPlayButton")!.IsEnabled = true;
        this.FindControl<Button>("SaveWavButton")!.IsEnabled = true;
        this.FindControl<Button>("RunExampleSetButton")!.IsEnabled = true;
    }

    /// <summary>
    /// Synthesize and discard a short utterance to prime the model's internal caches. Spike
    /// measured 1287 ms cold first-call latency vs 133-207 ms warm — pre-warming on app boot
    /// keeps the first real readback responsive. M10.3 will move this into
    /// <c>SherpaOnnxPilotVoice</c>'s constructor as a background Task.
    /// </summary>
    private async Task PrewarmAsync()
    {
        if (_tts is null)
        {
            return;
        }

        SetStatus("Pre-warming...");
        try
        {
            var sw = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                var gen = new OfflineTtsGenerationConfig { Sid = 0, Speed = 1.0f };
                _ = _tts.GenerateWithConfig("warmup", gen, null);
            });
            sw.Stop();
            AppendLog($"Pre-warm: {sw.ElapsedMilliseconds} ms (model now warm).");
            SetStatus("Pre-warmed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Pre-warm failed: {ex.Message}");
            AppendLog(ex.ToString());
        }
    }

    private async Task SynthAndPlayAsync()
    {
        if (_tts is null)
        {
            SetStatus("Load a model first.");
            return;
        }

        var text = (this.FindControl<TextBox>("TextBox")!.Text ?? "").Trim();
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("Empty text.");
            return;
        }

        int sid = (int)this.FindControl<Slider>("SpeakerIdSlider")!.Value;
        float speed = (float)this.FindControl<Slider>("SpeedSlider")!.Value;

        SetStatus("Synthesizing...");
        try
        {
            var result = await Task.Run(() => SynthOneAndApplyFx(text, sid, speed));
            _lastSamples = result.Samples;
            _lastSampleRate = result.SampleRate;
            // Save to a deterministic temp WAV so the user can play it externally / drag it out.
            _lastWavPath = SaveTempWav(result.Samples, result.SampleRate, sid);
            this.FindControl<TextBlock>("LastWavPathText")!.Text = _lastWavPath;
            await PlayAsync(result.Samples, result.SampleRate);
            SetStatus("Done.");
        }
        catch (Exception ex)
        {
            SetStatus($"Synth failed: {ex.Message}");
            AppendLog(ex.ToString());
        }
    }

    private readonly record struct SynthResult(float[] Samples, int SampleRate, int LatencyMs);

    private SynthResult SynthOneAndApplyFx(string text, int sid, float speed)
    {
        var gen = new OfflineTtsGenerationConfig { Sid = sid, Speed = speed };
        var sw = Stopwatch.StartNew();
        var audio = _tts!.GenerateWithConfig(text, gen, null);
        sw.Stop();
        int latencyMs = (int)sw.ElapsedMilliseconds;
        int sampleRate = _tts.SampleRate;

        float[] samples = audio.Samples;
        double durationSec = samples.Length / (double)sampleRate;
        double rtf = sw.Elapsed.TotalSeconds / Math.Max(durationSec, 1e-6);

        bool fxOn = this.FindControl<CheckBox>("RadioFxCheckBox")!.IsChecked == true;
        if (fxOn)
        {
            samples = ApplyRadioFx(samples, sampleRate);
        }

        Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<TextBlock>("LatencyText")!.Text =
                $"sid={sid} speed={speed:F2} synth={latencyMs} ms audio={durationSec:F2}s rtf={rtf:F2} fx={fxOn}";
            AppendLog($"sid={sid, 3} speed={speed:F2} synth={latencyMs, 4} ms audio={durationSec:F2}s rtf={rtf:F2} fx={fxOn}: \"{text}\"");
        });

        return new SynthResult(samples, sampleRate, latencyMs);
    }

    private float[] ApplyRadioFx(float[] input, int sampleRate)
    {
        float bpCenter = (float)this.FindControl<Slider>("BpCenterSlider")!.Value;
        float bpQ = (float)this.FindControl<Slider>("BpQSlider")!.Value;
        float drive = (float)this.FindControl<Slider>("DriveSlider")!.Value;
        int squelchMs = (int)this.FindControl<Slider>("SquelchSlider")!.Value;

        var bp1 = BiQuadFilter.BandPassFilterConstantPeakGain(sampleRate, bpCenter, bpQ);
        var bp2 = BiQuadFilter.BandPassFilterConstantPeakGain(sampleRate, bpCenter, bpQ);
        var hp = BiQuadFilter.HighPassFilter(sampleRate, 200f, 0.7f);

        int squelchTailSamples = (int)(sampleRate * (squelchMs / 1000.0));
        var output = new float[input.Length + squelchTailSamples];

        for (int i = 0; i < input.Length; i++)
        {
            float x = input[i];
            x = hp.Transform(x);
            x = bp1.Transform(x);
            x = bp2.Transform(x);
            x *= drive;
            x = (float)Math.Tanh(x);
            output[i] = x * 0.85f;
        }

        if (squelchTailSamples > 0)
        {
            var rng = new Random(42);
            for (int i = 0; i < squelchTailSamples; i++)
            {
                float t = i / (float)squelchTailSamples;
                float envelope = (1f - t) * 0.06f;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                noise = bp1.Transform(noise);
                output[input.Length + i] = noise * envelope;
            }
        }

        return output;
    }

    private async Task RunExampleSetAsync()
    {
        if (_tts is null)
        {
            return;
        }

        (string label, string text)[] utterances =
        [
            ("approach-cleared", "Cleared ILS twenty eight right approach, American twelve thirty four."),
            ("descend", "Descend and maintain five thousand, November one two three alpha bravo."),
            ("ready-taxi", "Oakland Ground, November one two three alpha bravo at the FBO ramp, ready to taxi."),
            ("handoff", "Departure on one two five point three five, American twelve thirty four, good day."),
            ("readback-takeoff", "Cleared for takeoff runway two eight right, American twelve thirty four."),
        ];
        int[] sids = [50, 142, 287, 360, 451];

        SetStatus("Running example set...");
        for (int i = 0; i < utterances.Length; i++)
        {
            var (label, text) = utterances[i];
            int sid = sids[i];
            try
            {
                var result = await Task.Run(() => SynthOneAndApplyFx(text, sid, 1.0f));
                var path = SaveTempWav(result.Samples, result.SampleRate, sid, label);
                Dispatcher.UIThread.Post(() => this.FindControl<TextBlock>("LastWavPathText")!.Text = path);
            }
            catch (Exception ex)
            {
                AppendLog($"Example #{i + 1} failed: {ex.Message}");
            }
        }
        SetStatus("Example set done. WAVs saved to %LOCALAPPDATA%/yaat/sandbox/tts/.");
    }

    private async Task SaveLastWavAsync()
    {
        if (_lastSamples is null || _lastSampleRate == 0)
        {
            SetStatus("No synth output to save — synthesize first.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var picked = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save TTS sandbox WAV",
                SuggestedFileName = $"tts-sandbox-{DateTime.Now:yyyyMMdd-HHmmss}.wav",
                DefaultExtension = "wav",
                FileTypeChoices = [new FilePickerFileType("WAV") { Patterns = ["*.wav"] }],
            }
        );
        if (picked is null)
        {
            return;
        }

        try
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(_lastSampleRate, 1);
            using var writer = new WaveFileWriter(picked.Path.LocalPath, format);
            writer.WriteSamples(_lastSamples, 0, _lastSamples.Length);
            AppendLog($"Saved {_lastSamples.Length} samples to {picked.Path.LocalPath}");
            SetStatus("Saved.");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
            AppendLog(ex.ToString());
        }
    }

    private static string SaveTempWav(float[] samples, int sampleRate, int sid, string? label = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat", "sandbox", "tts");
        Directory.CreateDirectory(dir);
        var name = label is null ? $"tts-sid{sid}-{DateTime.Now:HHmmss}.wav" : $"{label}-sid{sid}.wav";
        var path = Path.Combine(dir, name);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private async Task PlayAsync(float[] samples, int sampleRate)
    {
        StopActiveStream();
        EnsurePortAudio();

        int outDev = PortAudio.DefaultOutputDevice;
        if (outDev == PortAudio.NoDevice)
        {
            AppendLog("No default output device — skipping playback.");
            return;
        }

        var devInfo = PortAudio.GetDeviceInfo(outDev);
        var outParams = new StreamParameters
        {
            device = outDev,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = devInfo.defaultLowOutputLatency,
        };

        int cursor = 0;
        var cts = new CancellationTokenSource();
        _activeStreamCts = cts;

        StreamCallbackResult Callback(
            IntPtr input,
            IntPtr output,
            uint frameCount,
            ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags,
            IntPtr userData
        )
        {
            if (cts.IsCancellationRequested)
            {
                ZeroFill(output, (int)frameCount);
                return StreamCallbackResult.Complete;
            }

            int remaining = samples.Length - cursor;
            int toCopy = (int)Math.Min(frameCount, remaining);
            if (toCopy > 0)
            {
                System.Runtime.InteropServices.Marshal.Copy(samples, cursor, output, toCopy);
                cursor += toCopy;
            }

            if (toCopy < frameCount)
            {
                int zeroFloats = (int)frameCount - toCopy;
                IntPtr zeroDest = IntPtr.Add(output, toCopy * sizeof(float));
                ZeroFill(zeroDest, zeroFloats);
                return StreamCallbackResult.Complete;
            }
            return StreamCallbackResult.Continue;
        }

        var stream = new PortAudioSharp.Stream(
            inParams: null,
            outParams: outParams,
            sampleRate: sampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff,
            callback: Callback,
            userData: IntPtr.Zero
        );

        _activeStream = stream;
        stream.Start();
        AppendLog($"Playing {samples.Length / (double)sampleRate:F2}s through {devInfo.name}");

        // Wait for playback to finish or be canceled.
        double durationSec = samples.Length / (double)sampleRate;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSec + 0.3), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped by user.
        }

        try
        {
            stream.Stop();
        }
        catch
        { /* swallowed — stop is best-effort on cancel */
        }
        try
        {
            stream.Dispose();
        }
        catch
        { /* same */
        }
        if (ReferenceEquals(_activeStream, stream))
        {
            _activeStream = null;
            _activeStreamCts = null;
        }
    }

    private static void ZeroFill(IntPtr dest, int floatCount)
    {
        var zeros = new float[floatCount];
        System.Runtime.InteropServices.Marshal.Copy(zeros, 0, dest, floatCount);
    }

    private void StopActiveStream()
    {
        _activeStreamCts?.Cancel();
        _activeStreamCts = null;
    }

    private void EnsurePortAudio()
    {
        if (_portAudioInitialized)
        {
            return;
        }

        PortAudio.Initialize();
        _portAudioInitialized = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopActiveStream();
        try
        {
            _activeStream?.Dispose();
        }
        catch
        { /* shutdown best-effort */
        }
        _tts?.Dispose();
        if (_portAudioInitialized)
        {
            try
            {
                PortAudio.Terminate();
            }
            catch
            { /* shutdown best-effort */
            }
            _portAudioInitialized = false;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void SetStatus(string s) => this.FindControl<TextBlock>("TtsStatusText")!.Text = s;

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        var box = this.FindControl<TextBox>("TtsLogBox")!;
        box.Text = _log.ToString();
        box.CaretIndex = box.Text!.Length;
    }
}

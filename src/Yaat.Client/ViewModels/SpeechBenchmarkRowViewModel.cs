using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.ViewModels;

/// <summary>Which model, mode and device a benchmark row runs.</summary>
/// <param name="Engine">The model.</param>
/// <param name="Mode">The transcription mode.</param>
/// <param name="Device">Where it runs.</param>
public sealed record SpeechBenchmarkRowKey(SttEngine Engine, SttMode Mode, SttDevice Device);

/// <summary>
/// One row of the speech benchmark table: this machine's figures for one model, mode and device beside the reference WER and
/// callsign recall, or the one line saying why there are no figures (skipped, failed, waiting or measuring).
/// </summary>
public sealed partial class SpeechBenchmarkRowViewModel : ObservableObject
{
    private SpeechBenchmarkRowViewModel(SpeechBenchmarkRowKey key, SttReferenceEntry? reference, string? message, CorpusWerTiming? timing)
    {
        Key = key;
        MessageText = message;
        ModelText = SttModelCardViewModel.TitleOf(key.Engine);
        ModeText = key.Mode.ToString();
        DeviceText = SttBenchText.Device(key.Device);
        LoadText = (timing is null) ? "" : SttBenchText.Load(timing);
        P50Text = (timing is null) ? "" : SttBenchText.Milliseconds(timing.LatencyP50Ms);
        P90Text = (timing is null) ? "" : SttBenchText.Milliseconds(timing.LatencyP90Ms);
        RamText = (timing is null) ? "" : SttBenchText.Bytes(timing.PeakWorkingSetBytes);
        VramText =
            (timing is null) ? ""
            : (key.Device == SttDevice.Cpu) ? SttBenchText.Dash
            : SttBenchText.Bytes(timing.PeakGpuBytes);
        WerText = SttBenchText.Wer(reference?.OwnerWer);
        RecallText = SttBenchText.Recall(reference?.CallsignRecall);
    }

    /// <summary>The model, mode and device of the row.</summary>
    public SpeechBenchmarkRowKey Key { get; }

    public string ModelText { get; }

    public string ModeText { get; }

    public string DeviceText { get; }

    public string LoadText { get; }

    public string P50Text { get; }

    public string P90Text { get; }

    public string RamText { get; }

    /// <summary>The peak GPU memory; a dash on a CPU row.</summary>
    public string VramText { get; }

    public string WerText { get; }

    public string RecallText { get; }

    /// <summary>What the measured columns show in place of the figures; null when the figures show.</summary>
    public string? MessageText { get; }

    /// <summary>The row ran and failed.</summary>
    public bool IsFailed { get; private init; }

    /// <summary>The row was skipped; it is drawn at reduced opacity.</summary>
    public bool IsGreyed { get; private init; }

    /// <summary>The row is running now.</summary>
    public bool IsMeasuring { get; private init; }

    /// <summary>The row has figures and its model is installed now, so it can be applied.</summary>
    public bool IsPickable { get; private init; }

    /// <summary>The row is the model, mode and device the preferences save.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>A row that ran: its figures when it succeeded, its failure when not.</summary>
    /// <param name="key">The model, mode and device.</param>
    /// <param name="result">What it measured or why it failed.</param>
    /// <param name="reference">The reference figures; null when there are none.</param>
    /// <param name="isInstalled">Whether the row's model is installed now.</param>
    public static SpeechBenchmarkRowViewModel FromResult(
        SpeechBenchmarkRowKey key,
        SttBenchResult result,
        SttReferenceEntry? reference,
        bool isInstalled
    ) =>
        result is { Succeeded: true, Timing: { } timing }
            ? new(key, reference, null, timing) { IsPickable = isInstalled }
            : new(key, reference, $"failed: {result.Failure}", null) { IsFailed = true };

    /// <summary>A row that was not run, with <paramref name="reason"/>.</summary>
    public static SpeechBenchmarkRowViewModel FromSkip(SpeechBenchmarkRowKey key, string reason, SttReferenceEntry? reference) =>
        new(key, reference, reason, null) { IsGreyed = true };

    /// <summary>A row of the current run that has not started.</summary>
    public static SpeechBenchmarkRowViewModel Waiting(SpeechBenchmarkRowKey key, SttReferenceEntry? reference) =>
        new(key, reference, "waiting", null);

    /// <summary>The row of the current run that is running now.</summary>
    public static SpeechBenchmarkRowViewModel Measuring(SpeechBenchmarkRowKey key, SttReferenceEntry? reference) =>
        new(key, reference, "measuring…", null) { IsMeasuring = true };
}

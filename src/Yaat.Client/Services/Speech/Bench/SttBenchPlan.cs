using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>One row of a benchmark run.</summary>
/// <param name="Engine">The model the row loads.</param>
/// <param name="Mode">The mode it runs in.</param>
/// <param name="Device">Where it runs.</param>
/// <param name="SkipReason">Why the row is shown but not run; null when it runs.</param>
public sealed record SttBenchPlanRow(SttEngine Engine, SttMode Mode, SttDevice Device, string? SkipReason);

/// <summary>Works out which rows a benchmark run holds.</summary>
public static class SttBenchPlan
{
    private const string NoGpuRuntimeReason = "needs the GPU runtime (not installed)";
    private const string NoAccurateReason = "Accurate mode cannot run on this machine";

    /// <summary>
    /// Every downloaded rasr model, in the order given, by Accurate then Fast, by CPU then GPU. A row that cannot run stays
    /// in the list with its reason. Whisper is never benchmarked, so it gets no row.
    /// </summary>
    /// <param name="models">The downloaded models, in manifest order.</param>
    /// <param name="accurateUnavailableReason">Why Accurate mode cannot run; null when it can.</param>
    /// <param name="gpuUnavailableReason">Why the GPU rows cannot run; null when they can.</param>
    public static IReadOnlyList<SttBenchPlanRow> Build(
        IReadOnlyList<SttEngine> models,
        string? accurateUnavailableReason,
        string? gpuUnavailableReason
    )
    {
        var rows = new List<SttBenchPlanRow>();
        foreach (SttEngine engine in models.Where(model => model.ManifestModelId() is not null))
        {
            foreach (SttMode mode in (SttMode[])[SttMode.Accurate, SttMode.Fast])
            {
                foreach (SttDevice device in (SttDevice[])[SttDevice.Cpu, SttDevice.Gpu])
                {
                    string? modeReason = (mode == SttMode.Accurate) ? accurateUnavailableReason : null;
                    string? deviceReason = (device == SttDevice.Gpu) ? gpuUnavailableReason : null;
                    rows.Add(new SttBenchPlanRow(engine, mode, device, modeReason ?? deviceReason));
                }
            }
        }
        return rows;
    }

    /// <summary>The rows this machine can show now: its downloaded models, its n-best check and its GPU check.</summary>
    /// <param name="downloads">The app's speech-recognition downloads.</param>
    public static async Task<IReadOnlyList<SttBenchPlanRow>> FromDownloadsAsync(SttDownloads downloads)
    {
        (bool accurateAvailable, string? accurateReason) = await downloads.NbestAvailability.ConfigureAwait(false);
        GpuSupport gpu = await downloads.GpuSupport.ConfigureAwait(false);

        SttEngine[] engines = Enum.GetValues<SttEngine>();
        IEnumerable<SttEngine> installed = downloads
            .Models.Where(model => downloads.ModelInstaller(model.Id)?.IsInstalled == true)
            .SelectMany(model => engines.Where(engine => engine.ManifestModelId() == model.Id));

        string? gpuReason =
            !gpu.IsSupported ? gpu.Reason
            : (downloads.Gpu?.IsInstalled != true) ? NoGpuRuntimeReason
            : null;
        return Build([.. installed], accurateAvailable ? null : (accurateReason ?? NoAccurateReason), gpuReason);
    }
}

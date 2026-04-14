using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LMKit.Model;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Recommendation tier for an <see cref="LmKitModelEntry"/>. Used to annotate entries in the
/// Settings dropdowns so users can spot the YAAT-validated default without reading through the
/// full LM-Kit catalog. Everything else in the filtered set stays available as a fallback.
/// </summary>
public enum LmKitModelTier
{
    /// <summary>Standard — unlabeled entry in the picker.</summary>
    Standard,

    /// <summary>YAAT's validated default. Preselected when no user preference is set.</summary>
    Recommended,
}

/// <summary>
/// A single LM-Kit model presented to the Settings UI. Wraps a <see cref="ModelCard"/> with
/// observable download state so the picker can show Ready / Downloading / Not downloaded per row
/// and the user can trigger a download without leaving Settings. The underlying ModelCard is
/// authoritative for everything else (size, parameters, license, local path).
/// </summary>
public sealed partial class LmKitModelEntry : ObservableObject
{
    /// <summary>The LM-Kit ModelCard this entry wraps. Never null.</summary>
    public ModelCard Card { get; }

    /// <summary>LM-Kit model identifier (e.g. <c>gemma4:e4b</c>) passed to LoadFromModelID.</summary>
    public string ModelId => Card.ModelID;

    /// <summary>Friendly name: the ModelCard's ShortModelName with a recommendation badge appended for the default.</summary>
    public string DisplayName { get; }

    /// <summary>Approximate file size in megabytes. Sourced from <see cref="ModelCard.FileSize"/>.</summary>
    public int ApproxSizeMb => (int)(Card.FileSize / (1024 * 1024));

    /// <summary>Recommendation tier; drives highlight state and default selection.</summary>
    public LmKitModelTier Tier { get; }

    /// <summary>
    /// True when the model is large enough that CPU inference will be painful and a discrete
    /// GPU is strongly recommended. Heuristic: anything over 2 GB. Smaller models run fine on
    /// CPU for the single-shot command-mapping workload YAAT uses the LLM for.
    /// </summary>
    public bool GpuRecommended { get; }

    /// <summary>Short one-liner shown under the dropdown when this entry is selected.</summary>
    public string Description { get; }

    [ObservableProperty]
    private bool _isLocallyAvailable;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _statusMessage = "";

    public LmKitModelEntry(ModelCard card, string displayName, LmKitModelTier tier, string description)
    {
        Card = card;
        DisplayName = displayName;
        Tier = tier;
        Description = description;
        GpuRecommended = card.FileSize > 2L * 1024 * 1024 * 1024;
        _isLocallyAvailable = card.IsLocallyAvailable;
        _statusMessage = card.IsLocallyAvailable ? "Ready" : $"Not downloaded (~{ApproxSizeMb} MB)";
    }

    /// <summary>
    /// Downloads the model to LM-Kit's cache via <see cref="ModelCard.DownloadAsync"/>. Idempotent:
    /// LM-Kit returns immediately when the file already exists locally. Updates the observable
    /// progress / status / local-availability properties so the UI can react without extra wiring.
    /// </summary>
    public async Task<bool> DownloadAsync(CancellationToken ct)
    {
        if (IsDownloading)
        {
            return false;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        StatusMessage = "Starting download...";

        try
        {
            await Card.DownloadAsync(
                    (path, length, read) =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return false;
                        }
                        if (length.HasValue && length.Value > 0)
                        {
                            DownloadProgress = (double)read / length.Value;
                            StatusMessage = $"Downloading {DownloadProgress * 100:F0}% ({read / (1024 * 1024)} / {length.Value / (1024 * 1024)} MB)";
                        }
                        else
                        {
                            StatusMessage = $"Downloading {read / (1024 * 1024)} MB";
                        }
                        return true;
                    }
                )
                .ConfigureAwait(true);

            IsLocallyAvailable = Card.IsLocallyAvailable;
            StatusMessage = IsLocallyAvailable ? "Ready" : "Download did not complete";
            return IsLocallyAvailable;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled";
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = 0;
        }
    }
}

/// <summary>
/// Runtime catalog of LM-Kit STT and LLM models exposed to YAAT's Settings UI. Unlike a
/// hand-rolled static list, this queries <see cref="ModelCard.GetPredefinedModelCards"/> at
/// construction time so LM-Kit catalog additions automatically appear in YAAT without code
/// changes. Filters:
/// <list type="bullet">
///   <item><description>STT entries: must have <see cref="ModelCapabilities.SpeechToText"/>.</description></item>
///   <item><description>LLM entries: must have <see cref="ModelCapabilities.TextGeneration"/> AND
///     <see cref="ModelCapabilities.Chat"/>; excluded if the model's primary focus is Vision /
///     OCR / Translation (YAAT uses the LLM only for text-only canonical-command mapping); file
///     size between 500 MB and 16 GB (below 500 MB the model is too small for grammar-constrained
///     instruction following, above 16 GB it's impractical for typical dev boxes).</description></item>
/// </list>
/// The validated defaults (<c>whisper-large-turbo3</c> for STT, <c>gemma4:e4b</c> for LLM) are
/// flagged as <see cref="LmKitModelTier.Recommended"/>. The full filtered set stays available;
/// the UI preselects the recommended entry when no user preference exists.
/// </summary>
public static class LmKitModelCatalog
{
    private static readonly ILogger Log = AppLog.CreateLogger(nameof(LmKitModelCatalog));

    /// <summary>Validated default Whisper model ID. Preselected in the picker.</summary>
    public const string RecommendedWhisperId = "whisper-large-turbo3";

    /// <summary>Validated default LLM model ID. Preselected in the picker.</summary>
    public const string RecommendedLlmId = "gemma4:e4b";

    /// <summary>
    /// Builds the Whisper STT catalog. Cached on first call because
    /// <see cref="ModelCard.GetPredefinedModelCards"/> has non-trivial overhead and the predefined
    /// set doesn't change at runtime. The returned collection is observable so the UI can react to
    /// download state changes (<see cref="LmKitModelEntry.IsLocallyAvailable"/>, etc.).
    /// </summary>
    public static ObservableCollection<LmKitModelEntry> BuildWhisperCatalog()
    {
        return BuildCatalog(
            predicate: card => card.Capabilities.HasFlag(ModelCapabilities.SpeechToText),
            recommendedId: RecommendedWhisperId,
            descriptionFor: DescribeWhisper
        );
    }

    /// <summary>Builds the LLM catalog filtered for YAAT-relevant instruction-following models.</summary>
    public static ObservableCollection<LmKitModelEntry> BuildLlmCatalog()
    {
        return BuildCatalog(
            predicate: card =>
            {
                var cap = card.Capabilities;
                if (!cap.HasFlag(ModelCapabilities.TextGeneration) || !cap.HasFlag(ModelCapabilities.Chat))
                {
                    return false;
                }
                // Exclude OCR and Translation primaries — they're tuned for different workloads
                // and bring no advantage for command mapping. We KEEP models that have Vision as
                // a secondary capability (e.g. gemma3:4b) because their text generation is still
                // good for our use case.
                if (cap.HasFlag(ModelCapabilities.OCR) || cap.HasFlag(ModelCapabilities.Translation))
                {
                    return false;
                }
                // Size bounds: 500 MB floor (below this the model is too small for reliable
                // grammar-constrained instruction following), 16 GB ceiling (too large for
                // typical dev hardware).
                var sizeMb = card.FileSize / (1024 * 1024);
                return sizeMb is >= 500 and <= 16_384;
            },
            recommendedId: RecommendedLlmId,
            descriptionFor: DescribeLlm
        );
    }

    private static ObservableCollection<LmKitModelEntry> BuildCatalog(
        Func<ModelCard, bool> predicate,
        string recommendedId,
        Func<ModelCard, string> descriptionFor
    )
    {
        var result = new ObservableCollection<LmKitModelEntry>();
        try
        {
            var cards = ModelCard.GetPredefinedModelCards();
            foreach (var card in cards.Where(predicate).OrderBy(c => c.FileSize))
            {
                var isRecommended = string.Equals(card.ModelID, recommendedId, StringComparison.OrdinalIgnoreCase);
                var tier = isRecommended ? LmKitModelTier.Recommended : LmKitModelTier.Standard;
                var displayName = isRecommended ? $"{card.ShortModelName} ★ Recommended" : (card.ShortModelName ?? card.ModelName ?? card.ModelID);
                result.Add(new LmKitModelEntry(card, displayName, tier, descriptionFor(card)));
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to build LM-Kit model catalog");
        }
        return result;
    }

    private static string DescribeWhisper(ModelCard card)
    {
        var sizeMb = card.FileSize / (1024 * 1024);
        return $"{card.Publisher} · {card.License} · {card.ParameterCount / 1_000_000:N0} M parameters · {sizeMb:N0} MB";
    }

    private static string DescribeLlm(ModelCard card)
    {
        var sizeMb = card.FileSize / (1024 * 1024);
        var caps = new List<string>();
        if (card.Capabilities.HasFlag(ModelCapabilities.Reasoning))
        {
            caps.Add("Reasoning");
        }
        if (card.Capabilities.HasFlag(ModelCapabilities.ToolsCall))
        {
            caps.Add("ToolsCall");
        }
        if (card.Capabilities.HasFlag(ModelCapabilities.Math))
        {
            caps.Add("Math");
        }
        var capStr = caps.Count > 0 ? $" · {string.Join(", ", caps)}" : string.Empty;
        return $"{card.Publisher} · {card.License} · {card.ParameterCount / 1_000_000_000.0:F1} B parameters · {sizeMb:N0} MB{capStr}";
    }

    /// <summary>
    /// Finds the entry in <paramref name="catalog"/> whose <see cref="LmKitModelEntry.ModelId"/>
    /// matches <paramref name="modelId"/> case-insensitively. Returns null when the stored
    /// preference is a custom file path or URI not in the catalog — callers should treat null as
    /// "user has a custom source, leave the dropdown unselected" rather than an error.
    /// </summary>
    public static LmKitModelEntry? FindById(IEnumerable<LmKitModelEntry> catalog, string modelId)
    {
        return catalog.FirstOrDefault(e => string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Snapshot of the GPUs LM-Kit can see at runtime, used by the Settings UI to tell the user
/// whether the heavy catalog entries will actually accelerate. Computed lazily on first access
/// via <see cref="LMKit.Hardware.Gpu.GpuDeviceInfo.Devices"/>.
/// </summary>
public sealed record LmKitGpuSnapshot(IReadOnlyList<LmKitGpuDevice> Devices)
{
    /// <summary>True when LM-Kit detected at least one CUDA / Vulkan / Metal GPU device.</summary>
    public bool HasGpu => Devices.Count > 0;

    /// <summary>
    /// Free VRAM on the first detected GPU, in MB, or 0 when no GPU is detected. UI uses this
    /// to flag heavy LLM catalog entries as too large for the user's hardware.
    /// </summary>
    public int LargestFreeVramMb => Devices.Count == 0 ? 0 : Devices.Max(d => d.FreeMemoryMb);

    /// <summary>One-line summary suitable for a Settings panel header.</summary>
    public string Summary
    {
        get
        {
            if (Devices.Count == 0)
            {
                return "No compatible GPU detected — models will run on CPU.";
            }
            if (Devices.Count == 1)
            {
                var d = Devices[0];
                return $"GPU detected: {d.Description} ({d.FreeMemoryMb} MB free / {d.TotalMemoryMb} MB total)";
            }
            return $"{Devices.Count} GPUs detected. Largest free VRAM: {LargestFreeVramMb} MB.";
        }
    }
}

/// <summary>
/// One GPU device LM-Kit can see, projected from <see cref="LMKit.Hardware.Gpu.GpuDeviceInfo"/>
/// into a UI-friendly shape that doesn't leak LMKit types into the view layer.
/// </summary>
public sealed record LmKitGpuDevice(string Name, string Description, string DeviceType, int TotalMemoryMb, int FreeMemoryMb);

/// <summary>
/// Helper that wraps <see cref="LMKit.Hardware.Gpu.GpuDeviceInfo.Devices"/> in a defensive
/// try/catch so a startup-time enumeration failure can't crash the Settings UI. LM-Kit's device
/// enumeration touches native code on first call (CUDA driver, Vulkan loader); on a machine with
/// broken drivers this can throw, and we want the Settings panel to gracefully say "no GPU"
/// instead of dying.
/// </summary>
public static class LmKitGpuDetector
{
    public static LmKitGpuSnapshot Detect()
    {
        try
        {
            var devices = LMKit.Hardware.Gpu.GpuDeviceInfo.Devices;
            var projected = new List<LmKitGpuDevice>(capacity: devices.Count);
            foreach (var d in devices)
            {
                projected.Add(
                    new LmKitGpuDevice(
                        Name: d.DeviceName ?? "<unknown>",
                        Description: d.DeviceDescription ?? "<no description>",
                        DeviceType: d.DeviceType.ToString(),
                        TotalMemoryMb: (int)(d.TotalMemorySize / (1024 * 1024)),
                        FreeMemoryMb: (int)(d.FreeMemorySize / (1024 * 1024))
                    )
                );
            }
            return new LmKitGpuSnapshot(projected);
        }
        catch
        {
            // Driver / loader / initialization failure — fall back to "no GPU detected" so the
            // UI keeps working. We deliberately swallow the exception type because it's library
            // internals (CudaInteropException, dependency load failures) the user can't act on.
            return new LmKitGpuSnapshot([]);
        }
    }
}

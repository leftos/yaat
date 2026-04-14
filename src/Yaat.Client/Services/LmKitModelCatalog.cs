namespace Yaat.Client.Services;

/// <summary>
/// One entry in the curated catalog of LM-Kit models YAAT exposes through the Settings UI.
/// The <see cref="ModelId"/> is the LM-Kit identifier passed to <see cref="LMKit.Model.LM.LoadFromModelID"/>
/// and stored in <see cref="UserPreferences.LlmModelPath"/> / <see cref="UserPreferences.WhisperModelSize"/>.
/// </summary>
/// <param name="ModelId">LM-Kit model identifier — the string passed to LoadFromModelID.</param>
/// <param name="DisplayName">User-facing label for the dropdown.</param>
/// <param name="ApproxSizeMb">Approximate download / VRAM footprint in MB. Sourced from LM-Kit's own sample code annotations.</param>
/// <param name="Tier">Recommendation tier — drives default selection and helps the user pick.</param>
/// <param name="RequiresGpu">True if the model is impractical on CPU (large LLMs, large Whisper).</param>
/// <param name="Description">One-line blurb shown alongside the entry to help users choose.</param>
public sealed record LmKitModelEntry(string ModelId, string DisplayName, int ApproxSizeMb, LmKitModelTier Tier, bool RequiresGpu, string Description);

/// <summary>
/// Recommendation tier for an <see cref="LmKitModelEntry"/>. Used to sort the dropdown and
/// preselect a sensible default for users who haven't customized their model choice.
/// </summary>
public enum LmKitModelTier
{
    /// <summary>Smallest, fastest, lowest accuracy — runs on CPU on the cheapest hardware.</summary>
    Basic,

    /// <summary>YAAT's default. Best balance of accuracy and resource footprint.</summary>
    Recommended,

    /// <summary>Highest accuracy — needs a discrete GPU with substantial VRAM.</summary>
    Best,
}

/// <summary>
/// Curated catalog of LM-Kit STT and LLM models YAAT exposes in the Settings → Speech panel.
/// Entries are static because LM-Kit publishes a fixed set of model identifiers via
/// <see cref="LMKit.Model.LM.LoadFromModelID"/>; we mirror the ones that are appropriate for an
/// ATC training application (English-language Whisper variants and instruction-tuned LLMs in the
/// 4–14 B parameter range).
///
/// Sizes and VRAM estimates come from LM-Kit's own sample code (console_net/single_turn_chat
/// and console_net/audio_transcription), which is the canonical published source for these
/// numbers.
///
/// To add a new entry, append to <see cref="WhisperModels"/> or <see cref="LlmModels"/> with
/// the LM-Kit model ID and an appropriate tier — no further wiring is needed because
/// <see cref="LocalLlmService.EnsureLoaded"/> and <see cref="WhisperSttEngine.EnsureLoaded"/>
/// dispatch the configured ID through <c>LM.LoadFromModelID</c> uniformly.
/// </summary>
public static class LmKitModelCatalog
{
    /// <summary>
    /// Whisper STT variants. Sizes are LM-Kit's published VRAM requirements; on CPU the same
    /// model uses similar amounts of system RAM. <c>whisper-large-turbo3</c> is the YAAT default
    /// because the Scratch probe (probe-1.wav, probe-2.wav) showed it cleanly transcribes
    /// N-number callsigns the smaller variants mangle.
    /// </summary>
    public static IReadOnlyList<LmKitModelEntry> WhisperModels { get; } =
    [
        new(
            ModelId: "whisper-tiny",
            DisplayName: "Whisper Tiny",
            ApproxSizeMb: 50,
            Tier: LmKitModelTier.Basic,
            RequiresGpu: false,
            Description: "Smallest model. Fast on any hardware but mangles callsigns."
        ),
        new(
            ModelId: "whisper-base",
            DisplayName: "Whisper Base",
            ApproxSizeMb: 80,
            Tier: LmKitModelTier.Basic,
            RequiresGpu: false,
            Description: "Tiny CPU footprint. Acceptable for clean speech, not for tail-number callsigns."
        ),
        new(
            ModelId: "whisper-small",
            DisplayName: "Whisper Small",
            ApproxSizeMb: 260,
            Tier: LmKitModelTier.Basic,
            RequiresGpu: false,
            Description: "Better callsign accuracy than Base while still CPU-friendly."
        ),
        new(
            ModelId: "whisper-medium",
            DisplayName: "Whisper Medium",
            ApproxSizeMb: 820,
            Tier: LmKitModelTier.Recommended,
            RequiresGpu: false,
            Description: "Strong accuracy. Workable on modern CPUs; faster on any GPU."
        ),
        new(
            ModelId: "whisper-large-turbo3",
            DisplayName: "Whisper Large Turbo v3",
            ApproxSizeMb: 870,
            Tier: LmKitModelTier.Best,
            RequiresGpu: false,
            Description: "Best ATC accuracy. Recognizes tail-number callsigns cleanly. Recommended for GPU users."
        ),
    ];

    /// <summary>
    /// LLM variants for the <see cref="LocalLlmCommandMapper"/> fallback. The model only fires
    /// when <see cref="Yaat.Sim.Speech.PhraseologyMapper"/> returns null, so most PTT presses
    /// don't touch it — but when they do, the model needs to follow instructions well enough to
    /// produce a syntactically valid canonical command under the GBNF grammar constraint.
    /// </summary>
    public static IReadOnlyList<LmKitModelEntry> LlmModels { get; } =
    [
        new(
            ModelId: "qwen3.5:4b",
            DisplayName: "Qwen 3.5 4B",
            ApproxSizeMb: 2900,
            Tier: LmKitModelTier.Recommended,
            RequiresGpu: false,
            Description: "Default. Good instruction following at modest VRAM cost. Runs on CPU but slow."
        ),
        new(
            ModelId: "qwen3.5:9b",
            DisplayName: "Qwen 3.5 9B",
            ApproxSizeMb: 7000,
            Tier: LmKitModelTier.Best,
            RequiresGpu: true,
            Description: "Strong general accuracy. Needs ~7 GB VRAM."
        ),
        new(
            ModelId: "gemma4:e4b",
            DisplayName: "Gemma 4 E4B",
            ApproxSizeMb: 6000,
            Tier: LmKitModelTier.Best,
            RequiresGpu: true,
            Description: "Google's instruction-tuned 4-billion-parameter model. Needs ~6 GB VRAM."
        ),
        new(
            ModelId: "phi4",
            DisplayName: "Microsoft Phi-4 14.7B",
            ApproxSizeMb: 11000,
            Tier: LmKitModelTier.Best,
            RequiresGpu: true,
            Description: "Highest single-card accuracy in this catalog. Needs ~11 GB VRAM."
        ),
    ];

    /// <summary>
    /// Looks up an entry by its LM-Kit model ID. Returns null when the user has configured a
    /// custom model source (file path or URI) that isn't in the catalog.
    /// </summary>
    public static LmKitModelEntry? FindById(IReadOnlyList<LmKitModelEntry> catalog, string modelId)
    {
        foreach (var entry in catalog)
        {
            if (string.Equals(entry.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }
}

/// <summary>
/// Snapshot of the GPUs LM-Kit can see at runtime, used by the Settings UI to tell the user
/// whether the heavy <see cref="LmKitModelTier.Best"/> models will actually accelerate. Computed
/// lazily on first access via <see cref="LMKit.Hardware.Gpu.GpuDeviceInfo.Devices"/>.
/// </summary>
public sealed record LmKitGpuSnapshot(IReadOnlyList<LmKitGpuDevice> Devices)
{
    /// <summary>True when LM-Kit detected at least one CUDA / Vulkan / Metal GPU device.</summary>
    public bool HasGpu => Devices.Count > 0;

    /// <summary>
    /// Free VRAM on the first detected GPU, in MB, or 0 when no GPU is detected. UI uses this
    /// to flag <see cref="LmKitModelTier.Best"/> models as too large for the user's hardware.
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
/// <param name="Name">LM-Kit device name (e.g. "CUDA0").</param>
/// <param name="Description">Human-readable model name (e.g. "NVIDIA GeForce RTX 4090").</param>
/// <param name="DeviceType">"Gpu" / "IntegratedGpu" / "Accelerator" / "Cpu".</param>
/// <param name="TotalMemoryMb">Total VRAM in MB.</param>
/// <param name="FreeMemoryMb">Free VRAM in MB at snapshot time.</param>
public sealed record LmKitGpuDevice(string Name, string Description, string DeviceType, int TotalMemoryMb, int FreeMemoryMb);

/// <summary>
/// Helper that wraps <see cref="LMKit.Hardware.Gpu.GpuDeviceInfo.Devices"/> in a defensive
/// try/catch so a startup-time enumeration failure can't crash the Settings UI. LM-Kit's
/// device enumeration touches native code on first call (CUDA driver, Vulkan loader); on a
/// machine with broken drivers this can throw, and we want the Settings panel to gracefully
/// say "no GPU" instead of dying.
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

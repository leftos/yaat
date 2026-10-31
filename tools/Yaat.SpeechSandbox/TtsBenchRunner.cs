using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SherpaOnnx;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.SpeechSandbox;

/// <summary>Committed readback set for <c>--tts-bench</c>: each case is a callsign and canonical commands.</summary>
internal sealed record TtsBenchCorpus([property: JsonPropertyName("cases")] IReadOnlyList<TtsBenchCase> Cases);

/// <summary>One corpus case; <see cref="Facility"/> and <see cref="Frequency"/> are set only for a <c>CT</c> case.</summary>
internal sealed record TtsBenchCase(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("callsign")] string Callsign,
    [property: JsonPropertyName("commands")] string Commands,
    [property: JsonPropertyName("facility")] string? Facility,
    [property: JsonPropertyName("frequency")] double? Frequency
);

/// <summary>What a <c>--tts-bench-one</c> child process does: one timed phase of a model, or render the cloning references.</summary>
internal enum TtsBenchJobKind
{
    Phase,
    References,
}

/// <summary>
/// What one <c>--tts-bench-one</c> child process runs: one model's files, the thread count, the voices and lines,
/// whether to keep the clips, and where the clips, the references and the result go.
/// </summary>
internal sealed record TtsBenchJob(
    TtsBenchJobKind Kind,
    string ModelId,
    string ModelDir,
    string? VocoderPath,
    int Threads,
    IReadOnlyList<string> Voices,
    bool SaveClips,
    string ClipsRoot,
    string ReferenceDir,
    string ResultPath,
    IReadOnlyList<TtsBenchLine> Lines
);

[JsonSerializable(typeof(TtsBenchCorpus))]
[JsonSerializable(typeof(TtsBenchJob))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class TtsBenchCorpusJsonContext : JsonSerializerContext;

/// <summary>
/// Small-TTS benchmark: renders a committed set of pilot readbacks through <c>PilotResponder</c>, then for each model in
/// <see cref="TtsBenchModels"/> measures download and disk size and runs two phases. The <b>screen</b> times one voice on
/// the three <see cref="ScreenLineNames"/> at 8, 4 and 2 threads; a thread count passes when its p90 first audio is
/// <see cref="FirstAudioLimitMs"/> or less and its median RTF under <see cref="ScreenRtfLimit"/>, and a model with no
/// passing count is screened out. The <b>full run</b>, for a model that passes, times every voice on every line at the
/// lowest passing thread count and saves each clip dry and through <c>RadioAudioFx</c> for the listening page. Every
/// sherpa-onnx run is a child process, so a native abort or a hang costs one row. A model with rows in
/// <c>recorded-screens.json</c> is not run: those rows are its screen.
///
/// Run with <c>--tts-bench [--models a,b] [--download-only] [--corpus &lt;json&gt;]</c>. Models cache under
/// <c>YaatPaths.Combine("tts-bench", "models")</c>; clips go to <c>.tmp/tts-bench/clips/&lt;model&gt;/&lt;voice&gt;/&lt;nn&gt;-{dry,fx}.wav</c>;
/// <c>report.md</c> and <c>results.json</c> go to <c>docs/research/2026-10-24-tts-benchmark/</c> and the page to
/// <c>docs/plans/canvases/tts-benchmark/index.html</c>, rewritten after each model. Exit codes: 0 every selected model
/// ran or was screened out, 3 a model did not run, 2 usage, corpus, recorded-screens or download error.
/// </summary>
public static class TtsBenchRunner
{
    /// <summary>The production <c>PilotVoiceSpeechRate</c> default.</summary>
    public const float Speed = 1.1f;

    /// <summary>A screen row passes only when its p90 first-audio latency is at or under this.</summary>
    public const double FirstAudioLimitMs = 1500;

    /// <summary>A passing row whose p90 first audio is over this is flagged marginal.</summary>
    public const double MarginalFirstAudioMs = 1000;

    /// <summary>A screen row also needs a median RTF under this, so streamed playback never underruns.</summary>
    public const double ScreenRtfLimit = 1.0;

    /// <summary>At 8 threads, a median RTF at or above this skips the model's remaining thread counts.</summary>
    public const double HopelessRtf = 3.0;

    /// <summary>Load seconds over this screen a model out at once.</summary>
    public const double LoadLimitSeconds = 30;

    /// <summary>First audio on the first screen line over this screens a model out at once.</summary>
    public const double FirstLineFirstAudioLimitMs = 10_000;

    /// <summary>
    /// The verdict prefix of a row that stopped early on <see cref="LoadLimitSeconds"/> or
    /// <see cref="FirstLineFirstAudioLimitMs"/>.
    /// </summary>
    public const string ScreenedOutAtOnce = "screened out at once";

    /// <summary>Thread counts the screen tries, in order.</summary>
    public static readonly int[] ScreenThreads = [8, 4, 2];

    /// <summary>The corpus cases the screen speaks (one voice, the same three for every model).</summary>
    public static readonly string[] ScreenLineNames = ["speed-210", "cleared-for-takeoff", "squawk-4521"];

    internal const int ExitOk = 0;
    internal const int ExitSetupError = 2;
    private const int ExitModelNotRun = 3;

    private static readonly string[] ResultsDir = ["docs", "research", "2026-10-24-tts-benchmark"];

    private sealed record Options(IReadOnlyList<TtsBenchModel> Models, bool DownloadOnly, string CorpusPath);

    internal sealed record ModelAssets(string? Dir, string? VocoderPath, long? DownloadBytes, long? DiskBytes, string? Error);

    /// <summary>
    /// What every model's phases share: the repo root, the <c>.tmp/tts-bench</c> folder, why the cloning references
    /// could not be rendered (null when they were, or when no cloning model runs), the assets and the recorded screen rows.
    /// </summary>
    internal sealed record RunContext(
        string RepoRoot,
        string TmpRoot,
        string? ReferencesError,
        Dictionary<string, ModelAssets> Assets,
        IReadOnlyList<TtsBenchRecordedScreen> Recorded
    );

    public static async Task<int> RunAsync(string[] args)
    {
        string? repoRoot = OuroborosRunner.TryFindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine(
                $"FATAL: no repository root (a folder holding .git) above {AppContext.BaseDirectory}; run the sandbox built inside a yaat checkout."
            );
            return ExitSetupError;
        }
        Options? options = ParseArgs(args, repoRoot);
        if (options is null)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --tts-bench [--models <id>,<id>...] [--download-only] [--corpus <json>]");
            Console.Error.WriteLine($"Model ids: {string.Join(", ", TtsBenchModels.All.Select(m => m.Id))}");
            return ExitSetupError;
        }

        IReadOnlyList<TtsBenchLine>? lines = await RenderLinesAsync(options.CorpusPath).ConfigureAwait(false);
        IReadOnlyList<TtsBenchRecordedScreen>? recorded = await LoadRecordedScreensAsync(repoRoot).ConfigureAwait(false);
        if (lines is null || recorded is null)
        {
            return ExitSetupError;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var downloader = new TtsBenchDownloader(http, YaatPaths.Combine("tts-bench", "models"));
        Dictionary<string, ModelAssets> assets = await PrepareAssetsAsync(downloader, options.Models).ConfigureAwait(false);
        if (options.DownloadOnly)
        {
            return assets.Values.Any(a => a.Error is not null) ? ExitSetupError : ExitOk;
        }

        string tmpRoot = Path.Combine(repoRoot, ".tmp", "tts-bench");
        string? referencesError = options.Models.Any(m => m.IsCloning) ? await RenderReferencesAsync(assets, tmpRoot).ConfigureAwait(false) : null;
        var context = new RunContext(repoRoot, tmpRoot, referencesError, assets, recorded);
        return await RunModelsAsync(context, options.Models, lines).ConfigureAwait(false);
    }

    /// <summary>Screens and runs each model in turn, rewriting the outputs after each one so a later failure keeps the earlier results.</summary>
    private static async Task<int> RunModelsAsync(RunContext context, IReadOnlyList<TtsBenchModel> models, IReadOnlyList<TtsBenchLine> lines)
    {
        List<TtsBenchLine> screenLines = [.. lines.Where(l => ScreenLineNames.Contains(l.Name))];
        if (screenLines.Count != ScreenLineNames.Length)
        {
            Console.Error.WriteLine($"FATAL: the corpus lacks a screen line ({string.Join(", ", ScreenLineNames)}).");
            return ExitSetupError;
        }
        DateTime started = DateTime.UtcNow;
        TtsBenchMachine machine = DescribeMachine();
        string sherpaVersion = SherpaOnnxVersion();
        var modelResults = new List<TtsBenchModelResult>();
        foreach (TtsBenchModel model in models)
        {
            Console.WriteLine($"=== {model.Id} ===");
            TtsBenchModelResult result = await ScreenThenRunAsync(context, model, lines, screenLines).ConfigureAwait(false);
            Console.WriteLine(result.NotRun is null ? $"  {result.Clips.Count} clips at {result.Threads} threads" : $"  {result.NotRun}");
            modelResults.Add(result);
            var results = new TtsBenchResults(started, machine, sherpaVersion, Speed, ScreenLineNames, lines, modelResults);
            await WriteOutputsAsync(context.RepoRoot, results).ConfigureAwait(false);
        }
        return modelResults.Any(m => m.Verdict == TtsBenchVerdict.NotRun) ? ExitModelNotRun : ExitOk;
    }

    private static Options? ParseArgs(string[] args, string repoRoot)
    {
        IReadOnlyList<TtsBenchModel> models = TtsBenchModels.All;
        bool downloadOnly = false;
        string corpus = Path.Combine(repoRoot, "tools", "Yaat.SpeechSandbox", "Corpus", "tts-bench-readbacks.json");
        for (int i = 0; i < args.Length; i++)
        {
            bool hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--models" when hasValue:
                    string[] ids = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string[] unknown = [.. ids.Where(id => TtsBenchModels.All.All(m => m.Id != id))];
                    if (unknown.Length > 0)
                    {
                        Console.Error.WriteLine($"FATAL: unknown model id(s): {string.Join(", ", unknown)}");
                        return null;
                    }
                    models = [.. TtsBenchModels.All.Where(m => ids.Contains(m.Id))];
                    break;
                case "--download-only":
                    downloadOnly = true;
                    break;
                case "--corpus" when hasValue:
                    corpus = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"FATAL: unrecognised or malformed argument '{args[i]}'");
                    return null;
            }
        }
        return new Options(models, downloadOnly, corpus);
    }

    /// <summary>
    /// Renders every corpus case's readback against the real navdata the sim tests use (committed under
    /// <c>tests/Yaat.Sim.Tests/TestData</c>); prints and returns null when one fails to parse or renders empty.
    /// </summary>
    private static async Task<IReadOnlyList<TtsBenchLine>?> RenderLinesAsync(string corpusPath)
    {
        TtsBenchCorpus? corpus = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(corpusPath).ConfigureAwait(false),
            TtsBenchCorpusJsonContext.Default.TtsBenchCorpus
        );
        if (corpus is null || corpus.Cases.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: corpus {corpusPath} has no cases.");
            return null;
        }
        if (!SandboxNavData.TryInitialize(out string? navDataError))
        {
            Console.Error.WriteLine($"FATAL: {navDataError}");
            return null;
        }
        var lines = new List<TtsBenchLine>();
        foreach (TtsBenchCase c in corpus.Cases)
        {
            ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(c.Commands);
            string? text = parsed.IsSuccess ? Render(c, parsed.Value!) : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                string why = parsed.IsSuccess ? "the readback rendered empty" : $"the commands did not parse ({parsed.Reason})";
                Console.Error.WriteLine($"FATAL: corpus case '{c.Name}' (`{c.Commands}`): {why}.");
                return null;
            }
            lines.Add(new TtsBenchLine(lines.Count + 1, c.Name, c.Callsign, c.Commands, text));
            Console.WriteLine($"  {lines.Count:00} {c.Name}: \"{text}\"");
        }
        return lines;
    }

    /// <summary>
    /// The spoken (Tts) readback of one case. <c>BuildReadback</c> renders nothing for the frequency-change
    /// commands (the sim resolves those through the controller's position), so <c>CT</c> with the case's facility and
    /// frequency, and <c>FCA</c>, use <c>PilotResponder</c>'s dedicated contact and sign-off builders.
    /// </summary>
    private static string? Render(TtsBenchCase c, CompoundCommand compound)
    {
        var aircraft = new AircraftState { Callsign = c.Callsign, AircraftType = "B738" };
        string verb = c.Commands.Trim().ToUpperInvariant();
        if (verb == "CT" && c.Facility is not null && c.Frequency is { } mhz)
        {
            return PilotResponder.BuildContactReadback(aircraft, c.Facility, mhz, PilotSpeechContext.Canonical).Tts;
        }
        if (verb == "FCA")
        {
            return PilotResponder.BuildFrequencyChangeApproved(aircraft, PilotSpeechContext.Canonical).Tts;
        }
        return PilotResponder.BuildReadback(compound, aircraft, ActiveRunways.Empty)?.Tts;
    }

    /// <summary>Reads the committed <c>recorded-screens.json</c>; prints and returns null when it is missing or malformed.</summary>
    private static async Task<IReadOnlyList<TtsBenchRecordedScreen>?> LoadRecordedScreensAsync(string repoRoot)
    {
        string path = Path.Combine([repoRoot, .. ResultsDir, "recorded-screens.json"]);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FATAL: {path} is missing (it holds the screen rows kept from earlier runs; restore it from git).");
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(
                    await File.ReadAllTextAsync(path).ConfigureAwait(false),
                    TtsBenchJsonContext.Default.ListTtsBenchRecordedScreen
                ) ?? throw new JsonException("the file holds null");
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"FATAL: {path} is malformed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads every selected sherpa model (and the reference model and vocoders it needs) before any timing starts.</summary>
    private static async Task<Dictionary<string, ModelAssets>> PrepareAssetsAsync(TtsBenchDownloader downloader, IReadOnlyList<TtsBenchModel> models)
    {
        var needed = models.Where(m => m.Asset is not null).ToList();
        if (models.Any(m => m.IsCloning) && needed.All(m => m.Id != TtsBenchModels.ReferenceModelId))
        {
            needed.Add(TtsBenchModels.All.Single(m => m.Id == TtsBenchModels.ReferenceModelId));
        }
        var assets = new Dictionary<string, ModelAssets>(StringComparer.Ordinal);
        foreach (TtsBenchModel model in needed)
        {
            Console.WriteLine($"Preparing {model.Id}");
            try
            {
                assets[model.Id] = await PrepareModelAsync(downloader, model).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                Console.Error.WriteLine($"  {model.Id}: download failed: {ex.Message}");
                assets[model.Id] = new ModelAssets(null, null, null, null, $"download failed: {ex.Message}");
            }
        }
        return assets;
    }

    /// <summary>One model's folder, vocoder and sizes; a cached model or vocoder takes its byte count from the cache, not a HEAD request.</summary>
    private static async Task<ModelAssets> PrepareModelAsync(TtsBenchDownloader downloader, TtsBenchModel model)
    {
        string asset = model.Asset ?? throw new InvalidOperationException($"{model.Id} has no sherpa-onnx asset.");
        long bytes =
            downloader.CachedModelBytes(model.Id)
            ?? await downloader.ContentLengthAsync(TtsBenchModels.ModelBaseUrl + asset, CancellationToken.None).ConfigureAwait(false);
        string dir = await downloader.EnsureModelAsync(model.Id, asset, bytes, CancellationToken.None).ConfigureAwait(false);
        long disk = TtsBenchDownloader.DiskBytes(dir);
        if (TtsBenchModels.VocoderFor(model) is not { } vocoderName)
        {
            return new ModelAssets(dir, null, bytes, disk, null);
        }
        long vocoderBytes =
            downloader.CachedVocoderBytes(vocoderName)
            ?? await downloader.ContentLengthAsync(TtsBenchModels.VocoderBaseUrl + vocoderName, CancellationToken.None).ConfigureAwait(false);
        string vocoder = await downloader.EnsureVocoderAsync(vocoderName, vocoderBytes, CancellationToken.None).ConfigureAwait(false);
        return new ModelAssets(dir, vocoder, bytes + vocoderBytes, disk + new FileInfo(vocoder).Length, null);
    }

    /// <summary>Renders the cloning references in a child process; returns null on success, else why they could not be rendered.</summary>
    private static async Task<string?> RenderReferencesAsync(Dictionary<string, ModelAssets> assets, string tmpRoot)
    {
        ModelAssets piper = assets[TtsBenchModels.ReferenceModelId];
        if (piper.Dir is not { } dir)
        {
            return piper.Error ?? "the reference model is not downloaded";
        }
        TtsBenchModel model = TtsBenchModels.All.Single(m => m.Id == TtsBenchModels.ReferenceModelId);
        Console.WriteLine($"Rendering the cloning references with {model.Id}");
        string? error = await TtsBenchSynthesis.RenderReferencesAsync(model, dir, tmpRoot).ConfigureAwait(false);
        Console.WriteLine(error is null ? "  references rendered" : $"  references: {error}");
        return error;
    }

    /// <summary>
    /// Screens one model at 8, 4 and 2 threads (or takes its recorded rows), then gives it the full run at the lowest
    /// passing count, or a screened-out row.
    /// </summary>
    private static async Task<TtsBenchModelResult> ScreenThenRunAsync(
        RunContext context,
        TtsBenchModel model,
        IReadOnlyList<TtsBenchLine> lines,
        IReadOnlyList<TtsBenchLine> screenLines
    )
    {
        ModelAssets? assets = context.Assets.GetValueOrDefault(model.Id);
        List<TtsBenchScreenRow> rows = RecordedRows(context.Recorded, model.Id);
        if (rows.Count == 0)
        {
            rows = await ScreenLiveAsync(context, model, screenLines).ConfigureAwait(false);
        }

        TtsBenchScreenRow? passing = rows.Where(r => r.Outcome is TtsBenchScreenOutcome.Pass or TtsBenchScreenOutcome.Marginal)
            .OrderBy(r => r.Threads)
            .FirstOrDefault();
        if (passing is null)
        {
            bool anyRan = rows.Any(r => r.Outcome != TtsBenchScreenOutcome.NotRun);
            string reason = anyRan
                ? $"screened out at every thread count tried ({string.Join("; ", rows.Select(r => $"{r.Threads}: {r.Verdict}"))})"
                : rows[0].Verdict;
            return TtsBenchSynthesis.NotRun(model, reason, assets) with
            {
                Screen = rows,
                Verdict = anyRan ? TtsBenchVerdict.ScreenedOut : TtsBenchVerdict.NotRun,
            };
        }

        TtsBenchModelResult full = await RunPhaseAsync(context, model, new TtsBenchPhase(lines, model.Voices, passing.Threads, true))
            .ConfigureAwait(false);
        TtsBenchVerdict fullVerdict =
            full.NotRun is not null ? TtsBenchVerdict.NotRun
            : passing.Outcome == TtsBenchScreenOutcome.Marginal ? TtsBenchVerdict.Marginal
            : TtsBenchVerdict.Pass;
        return full with { DownloadBytes = assets?.DownloadBytes, DiskBytes = assets?.DiskBytes, Screen = rows, Verdict = fullVerdict };
    }

    /// <summary>Runs the screen at each of <see cref="ScreenThreads"/> until <see cref="StopsScreen"/> ends it.</summary>
    private static async Task<List<TtsBenchScreenRow>> ScreenLiveAsync(
        RunContext context,
        TtsBenchModel model,
        IReadOnlyList<TtsBenchLine> screenLines
    )
    {
        var rows = new List<TtsBenchScreenRow>();
        foreach (int threads in ScreenThreads)
        {
            TtsBenchScreenRow row = await ScreenAsync(context, model, screenLines, threads).ConfigureAwait(false);
            rows.Add(row);
            Console.WriteLine($"  screen @{threads} threads: {row.Verdict}");
            if (StopsScreen(row, threads))
            {
                break;
            }
        }
        return rows;
    }

    /// <summary>
    /// A count that did not run fails the same way at every count, a load or first line over its limit ends the screen
    /// at once, and a hopeless row at the first (highest) count cannot pass at fewer threads.
    /// </summary>
    private static bool StopsScreen(TtsBenchScreenRow row, int threads) =>
        row.Outcome is TtsBenchScreenOutcome.NotRun or TtsBenchScreenOutcome.ScreenedOutAtOnce
        || (threads == ScreenThreads[0] && row.MedianRtf >= HopelessRtf);

    /// <summary>
    /// A screen row passes when its p90 first audio is within <see cref="FirstAudioLimitMs"/> and its median RTF is
    /// under <see cref="ScreenRtfLimit"/>.
    /// </summary>
    private static bool Passes(TtsBenchScreenRow row) =>
        row.P90FirstAudioMs <= FirstAudioLimitMs && row.MedianRtf < ScreenRtfLimit && !(row.LoadSeconds > LoadLimitSeconds);

    /// <summary>
    /// The row with its outcome and verdict text: pass, marginal (p90 first audio over <see cref="MarginalFirstAudioMs"/>),
    /// or screened out with the reason.
    /// </summary>
    private static TtsBenchScreenRow Judge(TtsBenchScreenRow row)
    {
        if (Passes(row))
        {
            return row.P90FirstAudioMs > MarginalFirstAudioMs
                ? row with
                {
                    Outcome = TtsBenchScreenOutcome.Marginal,
                    Verdict = "marginal",
                }
                : row with
                {
                    Outcome = TtsBenchScreenOutcome.Pass,
                    Verdict = "pass",
                };
        }
        if (row.LoadSeconds is { } load && load > LoadLimitSeconds)
        {
            string loadText = load.ToString("F1", CultureInfo.InvariantCulture);
            return row with { Outcome = TtsBenchScreenOutcome.ScreenedOutAtOnce, Verdict = $"{ScreenedOutAtOnce}: load {loadText} s" };
        }
        var reasons = new List<string>();
        if (row.P90FirstAudioMs is not { } p90)
        {
            reasons.Add("first audio not measured");
        }
        else if (p90 > FirstAudioLimitMs)
        {
            reasons.Add($"p90 first audio {p90.ToString("F0", CultureInfo.InvariantCulture)} ms");
        }
        if (row.MedianRtf is { } rtf && rtf >= ScreenRtfLimit)
        {
            reasons.Add($"RTF {rtf.ToString("F2", CultureInfo.InvariantCulture)}");
        }
        return row with { Outcome = TtsBenchScreenOutcome.ScreenedOut, Verdict = $"screened out: {string.Join(", ", reasons)}" };
    }

    /// <summary>The model's rows from <c>recorded-screens.json</c>, highest thread count first; empty when it has none.</summary>
    private static List<TtsBenchScreenRow> RecordedRows(IReadOnlyList<TtsBenchRecordedScreen> recorded, string modelId) =>
        [.. recorded.Where(r => r.ModelId == modelId).OrderByDescending(r => r.Threads).Select(RecordedRow)];

    private static TtsBenchScreenRow RecordedRow(TtsBenchRecordedScreen recorded)
    {
        double[] synthMs = [.. recorded.Lines.Select(f => f.SynthSeconds * 1000).Order()];
        double[] rtf = [.. recorded.Lines.Select(f => f.SynthSeconds / f.AudioSeconds).Order()];
        double[]? firstMs = recorded.Lines.All(f => f.FirstAudioSeconds is not null)
            ? [.. recorded.Lines.Select(f => f.FirstAudioSeconds!.Value * 1000).Order()]
            : null;
        var row = new TtsBenchScreenRow(
            recorded.Threads,
            recorded.LoadSeconds,
            firstMs is null ? null : TtsBenchReport.Percentile(firstMs, 0.5),
            firstMs is null ? null : TtsBenchReport.Percentile(firstMs, 0.9),
            TtsBenchReport.Percentile(synthMs, 0.5),
            TtsBenchReport.Percentile(synthMs, 0.9),
            TtsBenchReport.Percentile(rtf, 0.5),
            TtsBenchScreenOutcome.NotRun,
            "",
            recorded.Source
        );
        return Judge(row);
    }

    private static async Task<TtsBenchScreenRow> ScreenAsync(
        RunContext context,
        TtsBenchModel model,
        IReadOnlyList<TtsBenchLine> screenLines,
        int threads
    )
    {
        TtsBenchModelResult run = await RunPhaseAsync(context, model, new TtsBenchPhase(screenLines, [model.Voices[0]], threads, false))
            .ConfigureAwait(false);
        TtsBenchSummary? s = TtsBenchReport.Summarize(run);
        var row = new TtsBenchScreenRow(
            threads,
            run.LoadSeconds,
            s?.MedianFirstAudioMs,
            s?.P90FirstAudioMs,
            s?.MedianSynthMs,
            s?.P90SynthMs,
            s?.MedianRtf,
            TtsBenchScreenOutcome.NotRun,
            run.NotRun ?? "",
            null
        );
        return run.Verdict switch
        {
            TtsBenchVerdict.NotRun => row,
            TtsBenchVerdict.ScreenedOut => row with { Outcome = TtsBenchScreenOutcome.ScreenedOutAtOnce },
            _ => Judge(row),
        };
    }

    /// <summary>One timed phase (screen or full run) of a model at a thread count: a sherpa-onnx child process or the Kitten-TTS 2 script.</summary>
    private static Task<TtsBenchModelResult> RunPhaseAsync(RunContext context, TtsBenchModel model, TtsBenchPhase phase)
    {
        if (model.Runtime == TtsRuntime.KittenTts2)
        {
            return TtsBenchSynthesis.RunKitten2Async(model, phase, context.TmpRoot, context.RepoRoot);
        }
        ModelAssets assets = context.Assets[model.Id];
        if (assets.Dir is not { } dir)
        {
            return Task.FromResult(TtsBenchSynthesis.NotRun(model, $"not run: {assets.Error ?? "not downloaded"}", assets));
        }
        if (model.IsCloning && context.ReferencesError is { } referencesError)
        {
            return Task.FromResult(
                TtsBenchSynthesis.NotRun(model, $"not run: the cloning references could not be rendered ({referencesError})", assets)
            );
        }
        return TtsBenchSynthesis.RunChildAsync(model, assets, dir, phase, context.TmpRoot);
    }

    /// <summary>The sherpa-onnx package version the sandbox runs (its informational version, else the assembly version).</summary>
    private static string SherpaOnnxVersion()
    {
        System.Reflection.Assembly sherpa = typeof(OfflineTts).Assembly;
        string? informational = sherpa
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion;
        return informational ?? sherpa.GetName().Version?.ToString() ?? "unknown";
    }

    private static TtsBenchMachine DescribeMachine()
    {
        string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
        if (OperatingSystem.IsWindows())
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"
            );
            if (key?.GetValue("ProcessorNameString") is string name)
            {
                cpu = name.Trim();
            }
        }
        return new TtsBenchMachine(
            cpu,
            Environment.ProcessorCount,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            "Gate slot: not recorded (the run cannot see it; docs/plans/canvases/tts-benchmark/RECIPE.md gives the alone invocation)"
        );
    }

    private static async Task WriteOutputsAsync(string repoRoot, TtsBenchResults results)
    {
        string resultsDir = Path.Combine([repoRoot, .. ResultsDir]);
        string pageDir = Path.Combine(repoRoot, "docs", "plans", "canvases", "tts-benchmark");
        Directory.CreateDirectory(resultsDir);
        Directory.CreateDirectory(pageDir);
        await File.WriteAllTextAsync(Path.Combine(resultsDir, "results.json"), TtsBenchReport.Serialize(results)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(resultsDir, "report.md"), TtsBenchReport.BuildReport(results)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(pageDir, "index.html"), TtsBenchReport.BuildPage(results)).ConfigureAwait(false);
        Console.WriteLine($"  wrote {resultsDir} and {Path.Combine(pageDir, "index.html")}");
    }
}

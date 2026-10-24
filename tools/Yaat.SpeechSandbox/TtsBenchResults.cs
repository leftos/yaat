using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.SpeechSandbox;

/// <summary>
/// One <c>--tts-bench</c> run: the machine, the generation settings, the screen lines, the rendered readback lines and
/// every model's measurements.
/// </summary>
internal sealed record TtsBenchResults(
    DateTime RunUtc,
    TtsBenchMachine Machine,
    string SherpaOnnxVersion,
    float Speed,
    IReadOnlyList<string> ScreenLines,
    IReadOnlyList<TtsBenchLine> Lines,
    IReadOnlyList<TtsBenchModelResult> Models
);

internal sealed record TtsBenchMachine(string Cpu, int LogicalCores, string Os, string GateSlot);

/// <summary>A readback line: the corpus case and the text <c>PilotResponder</c> rendered for it.</summary>
internal sealed record TtsBenchLine(int Index, string Name, string Callsign, string Commands, string Text);

/// <summary>Where a model ended: full run (pass or marginal), screened out as slower than real time, or not run.</summary>
internal enum TtsBenchVerdict
{
    Pass,
    Marginal,
    ScreenedOut,
    NotRun,
}

/// <summary>
/// How one screen row ended: it passed (or passed marginally), it ran and failed the limits, it stopped at once on
/// the load or first-line limit, or it did not run.
/// </summary>
internal enum TtsBenchScreenOutcome
{
    Pass,
    Marginal,
    ScreenedOut,
    ScreenedOutAtOnce,
    NotRun,
}

/// <summary>
/// One screen row: a model at one thread count, one voice, the three screen lines. <see cref="Verdict"/> is the
/// outcome's text with its reason; <see cref="Source"/> says where a row not measured in this run came from.
/// </summary>
internal sealed record TtsBenchScreenRow(
    int Threads,
    double? LoadSeconds,
    double? P50FirstAudioMs,
    double? P90FirstAudioMs,
    double? P50SynthMs,
    double? P90SynthMs,
    double? MedianRtf,
    TtsBenchScreenOutcome Outcome,
    string Verdict,
    string? Source
);

/// <summary>A screen row kept from an earlier run instead of a rerun (<c>recorded-screens.json</c>).</summary>
internal sealed record TtsBenchRecordedScreen(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("threads")] int Threads,
    [property: JsonPropertyName("loadSeconds")] double? LoadSeconds,
    [property: JsonPropertyName("lines")] IReadOnlyList<TtsBenchRecordedLine> Lines,
    [property: JsonPropertyName("source")] string Source
);

/// <summary>One recorded screen line: first-audio seconds (null when not logged), synthesis and audio seconds.</summary>
internal sealed record TtsBenchRecordedLine(
    [property: JsonPropertyName("firstAudioSeconds")] double? FirstAudioSeconds,
    [property: JsonPropertyName("synthSeconds")] double SynthSeconds,
    [property: JsonPropertyName("audioSeconds")] double AudioSeconds
);

/// <summary>
/// One model's results. The timing fields and <see cref="Clips"/> are the full run at <see cref="Threads"/>;
/// <see cref="NotRun"/> carries the reason when there was no full run (then those fields are null and the clips empty).
/// </summary>
internal sealed record TtsBenchModelResult(
    string Id,
    string Name,
    string Runtime,
    string License,
    string? NotRun,
    long? DownloadBytes,
    long? DiskBytes,
    int? Threads,
    double? LoadSeconds,
    double? WarmupMs,
    int? SampleRate,
    IReadOnlyList<string> Voices,
    string? Notes,
    IReadOnlyList<TtsBenchClip> Clips,
    IReadOnlyList<TtsBenchScreenRow> Screen,
    TtsBenchVerdict Verdict
);

/// <summary>
/// One synthesized clip. <see cref="FirstAudioMs"/> is the time from the call to the first callback (or streamed
/// chunk) carrying samples, null when none fired; <see cref="Rtf"/> is synthesis time over audio duration.
/// </summary>
internal sealed record TtsBenchClip(string Voice, int Line, double? FirstAudioMs, double SynthMs, double AudioSeconds, double Rtf);

/// <summary>
/// The summary row of a model: median and p90 first-audio latency (null when any clip's first audio was not
/// measured), median RTF, and median and p90 synthesis time.
/// </summary>
internal sealed record TtsBenchSummary(
    double? MedianFirstAudioMs,
    double? P90FirstAudioMs,
    double MedianRtf,
    double MedianSynthMs,
    double P90SynthMs
);

[JsonSerializable(typeof(TtsBenchResults))]
[JsonSerializable(typeof(TtsBenchModelResult))]
[JsonSerializable(typeof(List<TtsBenchRecordedScreen>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
internal sealed partial class TtsBenchJsonContext : JsonSerializerContext;

/// <summary>Writes <c>results.json</c>, <c>report.md</c> and the listening page from one <see cref="TtsBenchResults"/>.</summary>
internal static class TtsBenchReport
{
    public static string Serialize(TtsBenchResults results) =>
        JsonSerializer.Serialize(results, TtsBenchJsonContext.Default.TtsBenchResults).ReplaceLineEndings("\n") + "\n";

    public static TtsBenchSummary? Summarize(TtsBenchModelResult model)
    {
        if (model.Clips.Count == 0)
        {
            return null;
        }
        double[]? first = model.Clips.All(c => c.FirstAudioMs is not null) ? [.. model.Clips.Select(c => c.FirstAudioMs!.Value).Order()] : null;
        double[] rtf = [.. model.Clips.Select(c => c.Rtf).Order()];
        double[] synth = [.. model.Clips.Select(c => c.SynthMs).Order()];
        return new TtsBenchSummary(
            first is null ? null : Percentile(first, 0.5),
            first is null ? null : Percentile(first, 0.9),
            Percentile(rtf, 0.5),
            Percentile(synth, 0.5),
            Percentile(synth, 0.9)
        );
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    public static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static string Mb(long? bytes) => bytes is null ? "n/a" : (bytes.Value / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);

    private static string F(double? value, string format) => value is null ? "n/a" : value.Value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A first-audio figure in ms; a row with timings but no first audio says so instead of n/a.</summary>
    private static string FirstAudio(double? ms, bool timed) => ms is null && timed ? "not measured" : F(ms, "F0");

    /// <summary>The results table rows (shared by report.md and the page): one per model, the full run's figures or why there was none.</summary>
    private static IEnumerable<string[]> TableRows(TtsBenchResults results) =>
        results.Models.Select(m =>
        {
            TtsBenchSummary? s = Summarize(m);
            return new[]
            {
                m.Id,
                Mb(m.DownloadBytes),
                Mb(m.DiskBytes),
                m.Threads?.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                F(m.LoadSeconds, "F2"),
                FirstAudio(s?.MedianFirstAudioMs, s is not null),
                FirstAudio(s?.P90FirstAudioMs, s is not null),
                F(s?.MedianRtf, "F3"),
                m.NotRun ?? $"{VerdictText(m.Verdict)}, {m.Clips.Count} clips",
            };
        });

    private static readonly string[] TableHeader =
    [
        "Model",
        "Download MB",
        "Disk MB",
        "Threads",
        "Load s",
        "First audio median ms",
        "First audio p90 ms",
        "Median RTF",
        "Result",
    ];

    private static string VerdictText(TtsBenchVerdict verdict) =>
        verdict switch
        {
            TtsBenchVerdict.Pass => "pass",
            TtsBenchVerdict.Marginal => "marginal",
            TtsBenchVerdict.ScreenedOut => "screened out",
            _ => "not run",
        };

    private static void AppendTable(StringBuilder md, string[] header, IEnumerable<string[]> rows)
    {
        md.AppendLine("| " + string.Join(" | ", header) + " |");
        md.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", header.Length)));
        foreach (string[] row in rows)
        {
            md.AppendLine("| " + string.Join(" | ", row) + " |");
        }
        md.AppendLine();
    }

    private static void AppendScreen(StringBuilder md, TtsBenchResults results)
    {
        string lineNames = string.Join(", ", results.ScreenLines.Select(n => $"`{n}`"));
        md.AppendLine("## Screen");
        md.AppendLine();
        md.AppendLine(
            $"Each model speaks the screen lines {lineNames} with its first voice, after one warm-up, at "
                + $"{string.Join(", ", TtsBenchRunner.ScreenThreads)} threads (each count a cold load in its own process). "
                + $"A count passes when its p90 first audio is {F(TtsBenchRunner.FirstAudioLimitMs, "F0")} ms or less "
                + $"and its median RTF is under {F(TtsBenchRunner.ScreenRtfLimit, "F1")} (so streamed playback never underruns); "
                + $"a pass with p90 first audio over {F(TtsBenchRunner.MarginalFirstAudioMs, "F0")} ms is marginal. "
                + "A model passes when any count does, and its full run uses the lowest passing count. "
                + $"Load over {F(TtsBenchRunner.LoadLimitSeconds, "F0")} s or first audio over "
                + $"{F(TtsBenchRunner.FirstLineFirstAudioLimitMs / 1000, "F0")} s on the first line screens a model out at once. "
                + $"A count that does not run, or RTF {F(TtsBenchRunner.HopelessRtf, "F1")} or worse at "
                + $"{TtsBenchRunner.ScreenThreads[0]} threads, skips the model's other counts."
        );
        md.AppendLine();
        md.AppendLine(
            "Load time is a cold process each time, but only the first count tried reads the model from disk cold: "
                + $"the rows after {TtsBenchRunner.ScreenThreads[0]} threads load with the model files already in the OS file cache."
        );
        md.AppendLine();
        md.AppendLine(
            "First audio is what a streaming player would wait for. The shipped client calls `GenerateWithConfig` with no callback "
                + "(`PilotVoiceService.cs`) and plays only the finished clip, so in today's app the pilot's delay is the **total synthesis** time."
        );
        md.AppendLine();
        string[] header =
        [
            "Model",
            "Threads",
            "Load s",
            "First audio p50 ms",
            "First audio p90 ms",
            "Total synthesis p50 ms",
            "Total synthesis p90 ms",
            "Median RTF",
            "Verdict",
        ];
        IEnumerable<string[]> rows = results.Models.SelectMany(m =>
            m.Screen.Select(r =>
                new[]
                {
                    m.Id,
                    r.Threads.ToString(CultureInfo.InvariantCulture),
                    F(r.LoadSeconds, "F2"),
                    FirstAudio(r.P50FirstAudioMs, r.MedianRtf is not null),
                    FirstAudio(r.P90FirstAudioMs, r.MedianRtf is not null),
                    F(r.P50SynthMs, "F0"),
                    F(r.P90SynthMs, "F0"),
                    F(r.MedianRtf, "F3"),
                    r.Source is null ? r.Verdict : $"{r.Verdict} ({r.Source})",
                }
            )
        );
        AppendTable(md, header, rows);
    }

    private static void AppendModels(StringBuilder md, TtsBenchResults results)
    {
        md.AppendLine("## Models, voices and licences");
        md.AppendLine();
        foreach (TtsBenchModelResult m in results.Models)
        {
            string rate = m.SampleRate is null ? "" : $", {m.SampleRate} Hz";
            string threads = m.Threads is null ? "" : $" Full run at {m.Threads} threads ({VerdictText(m.Verdict)}).";
            md.AppendLine($"- **{m.Id}** ({m.Name}{rate}): voices {string.Join(", ", m.Voices)}. Licence: {m.License}.{threads}");
            if (m.Notes is not null)
            {
                md.AppendLine($"  {m.Notes}");
            }
            if (m.NotRun is not null)
            {
                md.AppendLine($"  {m.NotRun}");
            }
        }
        md.AppendLine();
    }

    public static string BuildReport(TtsBenchResults results)
    {
        var md = new StringBuilder();
        md.AppendLine("# Small TTS model benchmark (YAAT-1136)");
        md.AppendLine();
        md.AppendLine(
            $"Run {results.RunUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC by `Yaat.SpeechSandbox --tts-bench`; "
                + "every number below comes from `results.json` beside this file."
        );
        md.AppendLine();
        md.AppendLine("## Results");
        md.AppendLine();
        md.AppendLine(
            $"Full run: {results.Lines.Count} pilot readback lines × each passing model's voices, "
                + $"speed {results.Speed.ToString(CultureInfo.InvariantCulture)} (the production `PilotVoiceSpeechRate` default), "
                + "at the lowest thread count that passed the screen (the client runs 2). "
                + "Load time is the model constructor in a cold process (the model files are in the OS file cache by then); "
                + "one warm-up synthesis on the first voice is discarded. "
                + "First audio is the time from the call to the first callback carrying samples (\"not measured\" when no callback fired); "
                + "RTF is synthesis time over audio duration (below 1 is faster than real time). "
                + "A cloning model's warm-up uses only its first voice's reference, so the first line of each later voice "
                + "also carries the cost of encoding that voice's reference."
        );
        md.AppendLine();
        AppendTable(md, TableHeader, TableRows(results));
        md.AppendLine("Kokoro (en v0.19, multi-lang 1.0 and 1.1-zh) left out by the owner: too few voices for a pilot pool.");
        md.AppendLine();
        md.AppendLine(
            $"The sherpa-onnx models ran on sherpa-onnx {results.SherpaOnnxVersion} (the sandbox's package), each phase in its own child process. "
                + "The client ships 1.12.40, so Piper timings here may differ from the client's, and adopting any model means a client bump."
        );
        md.AppendLine();
        AppendScreen(md, results);
        AppendModels(md, results);
        md.AppendLine("## Machine");
        md.AppendLine();
        md.AppendLine($"- CPU: {results.Machine.Cpu}, {results.Machine.LogicalCores} logical cores");
        md.AppendLine($"- OS: {results.Machine.Os}");
        md.AppendLine($"- {results.Machine.GateSlot}");
        md.AppendLine("- A shared development PC: timings carry its background load and are comparable within this run, not across machines.");
        md.AppendLine();
        md.AppendLine("## Readback lines");
        md.AppendLine();
        foreach (TtsBenchLine line in results.Lines)
        {
            md.AppendLine($"{line.Index}. `{line.Commands}` ({line.Callsign}): \"{line.Text}\"");
        }
        md.AppendLine();
        md.AppendLine("The listening page is `docs/plans/canvases/tts-benchmark/` (its RECIPE.md rebuilds the clips).");
        return md.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>The listening page: the results table, a dry/FX toggle, then one row per line with an audio player per model × voice.</summary>
    public static string BuildPage(TtsBenchResults results)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><title>YAAT-1136 TTS benchmark</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font-family:system-ui,sans-serif;margin:1.5rem;background:#16181c;color:#e4e6ea}");
        html.AppendLine("table{border-collapse:collapse;margin-bottom:1rem}th,td{border:1px solid #3a3e46;padding:.25rem .5rem;font-size:.85rem}");
        html.AppendLine("th{background:#23262c;text-align:left}td.num{text-align:right}");
        html.AppendLine(
            ".line{border-top:1px solid #3a3e46;padding:.75rem 0}.text{font-size:1rem;margin-bottom:.5rem}.cmd{color:#9aa0aa;font-size:.8rem}"
        );
        html.AppendLine(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:.25rem .75rem}");
        html.AppendLine(".clip{font-size:.75rem;color:#b8bcc4}.clip audio{width:100%;height:28px}");
        html.AppendLine("#mode{position:sticky;top:0;background:#16181c;padding:.5rem 0;z-index:1}");
        html.AppendLine("</style></head><body>");
        html.AppendLine("<h1>Small TTS model benchmark (YAAT-1136)</h1>");
        html.AppendLine(
            $"<p>Run {results.RunUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC on {Esc(results.Machine.Cpu)} "
                + $"({results.Machine.LogicalCores} logical cores), speed {results.Speed.ToString(CultureInfo.InvariantCulture)}, "
                + "each model at the thread count in the table. "
                + "Clips are peak-normalised to -1 dBFS for listening; timings use the raw synthesis.</p>"
        );
        AppendPageTable(html, results);
        html.AppendLine(
            "<div id=\"mode\"><label><input type=\"radio\" name=\"fx\" value=\"dry\" checked> Dry</label> "
                + "<label><input type=\"radio\" name=\"fx\" value=\"fx\"> Radio FX</label></div>"
        );
        foreach (TtsBenchLine line in results.Lines)
        {
            AppendLineRow(html, results, line);
        }
        html.AppendLine("<script>");
        html.AppendLine("document.querySelectorAll('input[name=fx]').forEach(r=>r.addEventListener('change',()=>{");
        html.AppendLine("  const mode=document.querySelector('input[name=fx]:checked').value;");
        html.AppendLine("  document.querySelectorAll('audio[data-base]').forEach(a=>{a.pause();a.src=a.dataset.base+'-'+mode+'.wav';});");
        html.AppendLine("}));");
        html.AppendLine("</script>");
        html.AppendLine("</body></html>");
        return html.ToString().ReplaceLineEndings("\n");
    }

    private static void AppendPageTable(StringBuilder html, TtsBenchResults results)
    {
        html.Append("<table><tr>");
        foreach (string h in TableHeader)
        {
            html.Append($"<th>{Esc(h)}</th>");
        }
        html.AppendLine("</tr>");
        foreach (string[] row in TableRows(results))
        {
            html.Append("<tr>");
            for (int i = 0; i < row.Length; i++)
            {
                string cls = i is > 0 and < 7 ? " class=\"num\"" : "";
                html.Append($"<td{cls}>{Esc(row[i])}</td>");
            }
            html.AppendLine("</tr>");
        }
        html.AppendLine("</table>");
    }

    private static void AppendLineRow(StringBuilder html, TtsBenchResults results, TtsBenchLine line)
    {
        html.AppendLine("<div class=\"line\">");
        html.AppendLine(
            $"<div class=\"text\">{line.Index:00}. {Esc(line.Text)} <span class=\"cmd\">{Esc(line.Commands)} · {Esc(line.Callsign)}</span></div>"
        );
        html.AppendLine("<div class=\"grid\">");
        foreach (TtsBenchModelResult model in results.Models.Where(m => m.Clips.Count > 0))
        {
            foreach (TtsBenchClip clip in model.Clips.Where(c => c.Line == line.Index))
            {
                string basePath = $"clips/{model.Id}/{clip.Voice}/{line.Index:00}";
                html.AppendLine(
                    $"<div class=\"clip\">{Esc(model.Id)} · {Esc(clip.Voice)} · RTF {clip.Rtf.ToString("F2", CultureInfo.InvariantCulture)}"
                        + $"<audio controls preload=\"none\" data-base=\"{Esc(basePath)}\" src=\"{Esc(basePath)}-dry.wav\"></audio></div>"
                );
            }
        }
        html.AppendLine("</div></div>");
    }

    private static string Esc(string text) => WebUtility.HtmlEncode(text);
}

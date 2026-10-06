using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>One input action for the action log.</summary>
/// <param name="Pid">The client the action went to; only a recording of that process logs it.</param>
/// <param name="Kind"><c>click</c>, <c>hover</c> or <c>drag</c>.</param>
/// <param name="WallUtc">When the action was sent.</param>
/// <param name="Site">The window and the point (a drag's press point) in its DIPs, and its scale.</param>
/// <param name="ToX">A drag's release X in the same window's DIPs; null otherwise.</param>
/// <param name="ToY">A drag's release Y; null otherwise.</param>
/// <param name="Button">left, right or middle for a click or a drag; null for a hover.</param>
/// <param name="ClickCount">1 or 2 for a click; null otherwise.</param>
/// <param name="HoldMs">A drag's rest before the release; null otherwise.</param>
/// <param name="DurationMs">A hover's rest, or a drag's time from the press to the release; null for a click.</param>
/// <param name="Steps">A drag's moves; null otherwise.</param>
public sealed record RecordedAction(
    int Pid,
    string Kind,
    DateTime WallUtc,
    PointerSite Site,
    double? ToX,
    double? ToY,
    string? Button,
    int? ClickCount,
    int? HoldMs,
    int? DurationMs,
    int? Steps
)
{
    /// <summary>A click, by element or at a point.</summary>
    public static RecordedAction Click(int pid, DateTime wallUtc, PointerSite site, string button, int clickCount) =>
        new(pid, "click", wallUtc, site, null, null, button.ToLowerInvariant(), clickCount, null, null, null);

    /// <summary>A hover that rested <paramref name="durationMs"/>.</summary>
    public static RecordedAction Hover(int pid, DateTime wallUtc, PointerSite site, int durationMs) =>
        new(pid, "hover", wallUtc, site, null, null, null, null, null, durationMs, null);

    /// <summary>A drag as the client reported it.</summary>
    public static RecordedAction Drag(int pid, DateTime wallUtc, DragResult drag) =>
        new(pid, "drag", wallUtc, drag.From, drag.ToX, drag.ToY, drag.Button, null, drag.HoldMs, drag.DurationMs, drag.Steps);
}

/// <summary>
/// Writes <c>&lt;clip&gt;-actions.jsonl</c>, the input actions of a recording as JSON lines (UTF-8, LF, each line written and
/// flushed on its own). The first line is the header, <c>{"kind":"header","startedUtc","fps","cropX","cropY","cropWidth",
/// "cropHeight","renderScaling"}</c>: the first frame's instant (null when the recorder had not written it yet), the client-area
/// crop of each frame in pixels, and the window's DIP-to-pixel scale. Every later line is one action, <c>{"kind","wallUtc",
/// "clipSeconds","window","x","y","toX","toY","button","clickCount","holdMs","durationMs","steps"}</c>, in window DIPs, with
/// null for a field the kind does not have.
/// </summary>
public static class ActionLog
{
    private static readonly JsonSerializerOptions LineJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Starts the file at <paramref name="path"/> with the header line, replacing any file there.</summary>
    public static void WriteHeader(string path, DateTime? startedUtc, int fps, CropBox crop, double renderScaling)
    {
        var header = new HeaderLine("header", startedUtc, fps, crop.X, crop.Y, crop.Width, crop.Height, renderScaling);
        File.WriteAllText(path, JsonSerializer.Serialize(header, LineJson) + "\n", Utf8NoBom);
    }

    /// <summary>Seconds into the clip of an action at <paramref name="wallUtc"/>, to the millisecond.</summary>
    public static double ClipSeconds(DateTime wallUtc, DateTime startedUtc) => Math.Round((wallUtc - startedUtc).TotalSeconds, 3);

    /// <summary>
    /// Writes <paramref name="startedUtc"/>, the first frame's instant, into the header of a log written before it was known,
    /// and fills the clipSeconds of every line written before it; the file is rewritten whole through a temporary file.
    /// </summary>
    public static void WriteStart(string path, DateTime startedUtc)
    {
        var rewritten = new StringBuilder();
        foreach (string line in File.ReadAllLines(path, Utf8NoBom).Where(line => line.Length > 0))
        {
            JsonObject node = (JsonNode.Parse(line) ?? throw new InvalidDataException($"{path} holds a JSON null line")).AsObject();
            if (string.Equals((string?)node["kind"], "header", StringComparison.Ordinal))
            {
                node["startedUtc"] = startedUtc;
            }
            else if (node["clipSeconds"] is null)
            {
                node["clipSeconds"] = ClipSeconds(node["wallUtc"]!.GetValue<DateTime>(), startedUtc);
            }

            rewritten.Append(node.ToJsonString()).Append('\n');
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, rewritten.ToString(), Utf8NoBom);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Appends one action line; <paramref name="clipSeconds"/> is null before the first frame.</summary>
    public static void Append(string path, RecordedAction action, double? clipSeconds)
    {
        PointerSite site = action.Site;
        var line = new ActionLine(
            action.Kind,
            action.WallUtc,
            clipSeconds,
            site.Window,
            site.X,
            site.Y,
            action.ToX,
            action.ToY,
            action.Button,
            action.ClickCount,
            action.HoldMs,
            action.DurationMs,
            action.Steps
        );
        File.AppendAllText(path, JsonSerializer.Serialize(line, LineJson) + "\n", Utf8NoBom);
    }

    private sealed record HeaderLine(
        string Kind,
        DateTime? StartedUtc,
        int Fps,
        int CropX,
        int CropY,
        int CropWidth,
        int CropHeight,
        double RenderScaling
    );

    private sealed record ActionLine(
        string Kind,
        DateTime WallUtc,
        double? ClipSeconds,
        string Window,
        double X,
        double Y,
        double? ToX,
        double? ToY,
        string? Button,
        int? ClickCount,
        int? HoldMs,
        int? DurationMs,
        int? Steps
    );
}

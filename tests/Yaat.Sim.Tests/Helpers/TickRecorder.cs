using System.Globalization;
using System.Text;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Records per-tick aircraft state for visualization with Yaat.TickAnimator.
/// Attach to any test's simulation loop to capture position, heading, speed,
/// and phase data as a CSV file.
///
/// Usage:
///   var recorder = new TickRecorder(aircraft);
///
///   for (int t = 1; t &lt;= 300; t++)
///   {
///       engine.TickOneSecond();
///       recorder.Record(t);
///   }
///
///   recorder.WriteCsv(".tmp/oak-w6-exit.csv");
///
/// Then visualize:
///   dotnet run --project tools/Yaat.TickAnimator -- \
///     --layout tests/Yaat.Sim.Tests/TestData/oak.geojson \
///     --ticks .tmp/oak-w6-exit.csv \
///     --aircraft B738 \
///     --output .tmp/oak-w6-exit.gif
///
/// See docs/tick-animator.md for full documentation.
/// </summary>
public sealed class TickRecorder
{
    private readonly AircraftState _aircraft;
    private readonly List<TickRow> _rows = [];

    /// <summary>Optional filter: only record ticks where this returns true.</summary>
    public Func<AircraftState, bool>? Filter { get; set; }

    public TickRecorder(AircraftState aircraft)
    {
        _aircraft = aircraft;
    }

    /// <summary>
    /// Record the current aircraft state at the given time. Call once per tick
    /// after <c>engine.TickOneSecond()</c>.
    /// </summary>
    public void Record(int time)
    {
        if (Filter is not null && !Filter(_aircraft))
        {
            return;
        }

        var nav = _aircraft.LastNavDiag;
        _rows.Add(
            new TickRow
            {
                Time = time,
                Lat = _aircraft.Latitude,
                Lon = _aircraft.Longitude,
                Hdg = _aircraft.TrueHeading.Degrees,
                Gs = _aircraft.GroundSpeed,
                Phase = _aircraft.Phases?.CurrentPhase?.Name ?? "none",
                Twy = _aircraft.CurrentTaxiway ?? "",
                Nav = nav,
            }
        );
    }

    /// <summary>Number of ticks recorded so far.</summary>
    public int Count => _rows.Count;

    /// <summary>
    /// Write all recorded ticks to a CSV file. Creates parent directories if needed.
    /// </summary>
    public void WriteCsv(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            "t,lat,lon,hdg,gs,phase,twy,navTarget,navDist,navBrg,navAngleDiff,navTargetSpd,navBrakeLimit,navArcLimit,navOnArc,navNodeReqSpd"
        );
        foreach (var row in _rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{row.Time},");
            sb.Append(CultureInfo.InvariantCulture, $"{row.Lat:F8},");
            sb.Append(CultureInfo.InvariantCulture, $"{row.Lon:F8},");
            sb.Append(CultureInfo.InvariantCulture, $"{row.Hdg:F2},");
            sb.Append(CultureInfo.InvariantCulture, $"{row.Gs:F2},");
            sb.Append(CultureInfo.InvariantCulture, $"{row.Phase},");
            sb.Append(row.Twy);
            if (row.Nav is { } n)
            {
                sb.Append(CultureInfo.InvariantCulture, $",{n.TargetNodeId}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.DistToTargetNm:F4}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.BearingToTargetDeg:F1}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.AngleDiffDeg:F1}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.TargetSpeedKts:F1}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.BrakingLimitKts:F1}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.ArcSpeedLimitKts:F1}");
                sb.Append(CultureInfo.InvariantCulture, $",{(n.OnArc ? 1 : 0)}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.NodeRequiredSpeedKts:F1}");
            }

            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// Walk up from the current directory to find the repo root (contains yaat.slnx).
    /// Useful for writing CSVs to .tmp/ relative to the repo root regardless of
    /// where the test runner's working directory is.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private sealed class TickRow
    {
        public required int Time { get; init; }
        public required double Lat { get; init; }
        public required double Lon { get; init; }
        public required double Hdg { get; init; }
        public required double Gs { get; init; }
        public required string Phase { get; init; }
        public required string Twy { get; init; }
        public NavTickDiag? Nav { get; init; }
    }
}

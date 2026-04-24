using System.Globalization;
using System.Text;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

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
    /// Attach a recorder to <paramref name="engine"/>'s <see cref="SimulationEngine.TickCompleted"/>
    /// event, following <paramref name="callsign"/> (resolved at attach time and each tick so
    /// the recorder keeps working even if the aircraft is rehydrated). Writes the CSV to
    /// <paramref name="csvPath"/> on <see cref="IDisposable.Dispose"/>.
    ///
    /// Usage:
    ///   using var _ = TickRecorder.Attach(engine, "N9225L", ".tmp/n9225l.csv");
    ///
    /// The <c>using</c> guarantees the CSV is written even if the test asserts fail.
    /// </summary>
    public static IDisposable Attach(SimulationEngine engine, string callsign, string csvPath)
    {
        return new AttachedRecorder(engine, callsign, csvPath);
    }

    private sealed class AttachedRecorder : IDisposable
    {
        private readonly SimulationEngine _engine;
        private readonly string _callsign;
        private readonly string _csvPath;
        private readonly List<TickRow> _rows = [];
        private readonly Action<int> _handler;

        public AttachedRecorder(SimulationEngine engine, string callsign, string csvPath)
        {
            _engine = engine;
            _callsign = callsign;
            _csvPath = csvPath;
            _handler = OnTick;
            _engine.TickCompleted += _handler;
        }

        private void OnTick(int elapsedSeconds)
        {
            var ac = _engine.FindAircraft(_callsign);
            if (ac is null)
            {
                return;
            }

            _rows.Add(
                new TickRow
                {
                    Time = elapsedSeconds,
                    Lat = ac.Position.Lat,
                    Lon = ac.Position.Lon,
                    Hdg = ac.TrueHeading.Degrees,
                    Gs = ac.GroundSpeed,
                    Phase = ac.Phases?.CurrentPhase?.Name ?? "none",
                    Twy = ac.CurrentTaxiway ?? "",
                    Nav = ac.LastNavDiag,
                }
            );
        }

        public void Dispose()
        {
            _engine.TickCompleted -= _handler;
            WriteRowsCsv(_csvPath, _rows);
        }
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
                Lat = _aircraft.Position.Lat,
                Lon = _aircraft.Position.Lon,
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
    public void WriteCsv(string path) => WriteRowsCsv(path, _rows);

    private static void WriteRowsCsv(string path, IReadOnlyList<TickRow> rows)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            "t,lat,lon,hdg,gs,phase,twy,navTarget,navDist,navBrg,navAngleDiff,navTargetSpd,navBrakeLimit,navArcLimit,navOnArc,navNodeReqSpd,navPathDevFt,navSegFromLat,navSegFromLon"
        );
        foreach (var row in rows)
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
                sb.Append(CultureInfo.InvariantCulture, $",{n.PathDeviationFt:F1}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.SegFromLat:F8}");
                sb.Append(CultureInfo.InvariantCulture, $",{n.SegFromLon:F8}");
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

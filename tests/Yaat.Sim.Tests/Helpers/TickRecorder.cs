using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Records per-tick aircraft state for visualization with Yaat.LayoutInspector
/// (--ticks JSON overlay) and Yaat.TickAnimator. Produces a JSON document with
/// embedded aircraft metadata (type, wingspan, length, render color) so the
/// consumer doesn't need separate CLI flags per aircraft.
///
/// Single-aircraft usage:
///   var recorder = new TickRecorder(aircraft);
///   for (int t = 1; t &lt;= 300; t++)
///   {
///       engine.TickOneSecond();
///       recorder.Record(t);
///   }
///   recorder.WriteJson(".tmp/oak-w6-exit.json");
///
/// Multi-aircraft usage:
///   var recorder = new TickRecorder(n152sp, n569sx);
///   ... (Record() captures all attached aircraft per tick) ...
///   recorder.WriteJson(".tmp/conflict-fix.json");
///
/// Engine-attached usage (writes on Dispose):
///   using var _ = TickRecorder.Attach(engine, ".tmp/scenario.json", "N152SP", "N569SX");
///
/// Then visualize:
///   dotnet run --project tools/Yaat.LayoutInspector -- \
///     tests/Yaat.Sim.Tests/TestData/oak.geojson \
///     --ticks .tmp/conflict-fix.json --html .tmp/conflict-fix.html
/// </summary>
public sealed class TickRecorder
{
    /// <summary>JSON schema version. Bump on incompatible changes.</summary>
    public const int SchemaVersion = 1;

    private static readonly string[] DefaultPalette = ["#1e88e5", "#fb8c00", "#43a047", "#e53935", "#8e24aa", "#00acc1", "#fdd835", "#6d4c41"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<AircraftState> _aircraft;
    private readonly Dictionary<string, AircraftMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TickEvent> _ticks = [];
    private readonly string? _airportId;

    /// <summary>Optional per-aircraft filter: only record when this returns true.</summary>
    public Func<AircraftState, bool>? Filter { get; set; }

    /// <summary>
    /// Construct with one or more aircraft to record. Aircraft metadata
    /// (type, wingspan, length) is captured at construction and color is
    /// auto-assigned from a default palette in attach order.
    /// </summary>
    public TickRecorder(params AircraftState[] aircraft)
    {
        if (aircraft.Length == 0)
        {
            throw new ArgumentException("At least one aircraft is required", nameof(aircraft));
        }

        _aircraft = [.. aircraft];
        _airportId = aircraft[0].Ground.LayoutAirportId;
        for (int i = 0; i < aircraft.Length; i++)
        {
            CaptureMetadata(aircraft[i], DefaultPalette[i % DefaultPalette.Length]);
        }
    }

    /// <summary>
    /// Add another aircraft to the recorder mid-session. Color auto-assigned.
    /// </summary>
    public void AddAircraft(AircraftState aircraft)
    {
        _aircraft.Add(aircraft);
        CaptureMetadata(aircraft, DefaultPalette[(_metadata.Count - 1) % DefaultPalette.Length]);
    }

    /// <summary>
    /// Attach a recorder to <paramref name="engine"/>'s
    /// <see cref="SimulationEngine.TickCompleted"/> event, following each
    /// callsign in <paramref name="callsigns"/> (resolved each tick so it
    /// keeps working across rehydration). Writes the JSON to
    /// <paramref name="jsonPath"/> on <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public static IDisposable Attach(SimulationEngine engine, string jsonPath, params string[] callsigns)
    {
        return new AttachedRecorder(engine, jsonPath, callsigns);
    }

    /// <summary>
    /// Record the current state of all attached aircraft at <paramref name="time"/>.
    /// Call once per tick after <c>engine.TickOneSecond()</c>.
    /// </summary>
    public void Record(int time)
    {
        foreach (var ac in _aircraft)
        {
            if (Filter is not null && !Filter(ac))
            {
                continue;
            }

            _ticks.Add(BuildTickEvent(time, ac));
        }
    }

    /// <summary>Number of tick events recorded so far (across all aircraft).</summary>
    public int Count => _ticks.Count;

    /// <summary>
    /// Write the full recording (metadata + ticks) to a JSON file. Creates
    /// parent directories if needed.
    /// </summary>
    public void WriteJson(string path)
    {
        WriteJsonFile(path, BuildRecording());
    }

    /// <summary>
    /// Walk up from the current directory to find the repo root (contains yaat.slnx).
    /// Useful for writing output files to .tmp/ relative to the repo root regardless
    /// of where the test runner's working directory is.
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

    private void CaptureMetadata(AircraftState aircraft, string color)
    {
        if (_metadata.ContainsKey(aircraft.Callsign))
        {
            return;
        }

        var rec = FaaAircraftDatabase.Get(aircraft.AircraftType);
        _metadata[aircraft.Callsign] = new AircraftMetadata
        {
            Callsign = aircraft.Callsign,
            Type = aircraft.AircraftType,
            WingspanFt = rec?.WingspanFt,
            LengthFt = rec?.LengthFt,
            Color = color,
        };
    }

    private TickRecording BuildRecording() =>
        new()
        {
            Version = SchemaVersion,
            AirportId = _airportId,
            Aircraft = _metadata.Values.OrderBy(m => m.Callsign, StringComparer.Ordinal).ToList(),
            Ticks = _ticks,
        };

    private static TickEvent BuildTickEvent(int time, AircraftState ac) =>
        new()
        {
            T = time,
            Callsign = ac.Callsign,
            Lat = ac.Position.Lat,
            Lon = ac.Position.Lon,
            Hdg = ac.TrueHeading.Degrees,
            Gs = ac.GroundSpeed,
            Phase = ac.Phases?.CurrentPhase?.Name ?? "none",
            Twy = string.IsNullOrEmpty(ac.Ground.CurrentTaxiway) ? null : ac.Ground.CurrentTaxiway,
            SpeedLimit = ac.Ground.SpeedLimit,
            Nav = ac.Ground.LastNavDiag is { } n ? NavTickDto.From(n) : null,
            Ias = ac.IsOnGround ? null : ac.IndicatedAirspeed,
            Vs = ac.IsOnGround ? null : ac.VerticalSpeed,
            Alt = ac.IsOnGround ? null : ac.Altitude,
            TgtSpd = ac.Targets.TargetSpeed,
            Status = Yaat.Sim.AircraftStatusDescriber.Describe(ac, Yaat.Sim.AircraftStatusContext.None).Text,
        };

    private static void WriteJsonFile(string path, TickRecording recording)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(recording, JsonOptions));
    }

    private sealed class AttachedRecorder : IDisposable
    {
        private readonly SimulationEngine _engine;
        private readonly string _jsonPath;
        private readonly string[] _callsigns;
        private readonly List<TickEvent> _ticks = [];
        private readonly Dictionary<string, AircraftMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
        private readonly Action<int> _handler;
        private string? _airportId;

        public AttachedRecorder(SimulationEngine engine, string jsonPath, string[] callsigns)
        {
            _engine = engine;
            _jsonPath = jsonPath;
            _callsigns = callsigns;
            _handler = OnTick;
            _engine.TickCompleted += _handler;
        }

        private void OnTick(int elapsedSeconds)
        {
            for (int i = 0; i < _callsigns.Length; i++)
            {
                var ac = _engine.FindAircraft(_callsigns[i]);
                if (ac is null)
                {
                    continue;
                }

                _airportId ??= ac.Ground.LayoutAirportId;
                if (!_metadata.ContainsKey(ac.Callsign))
                {
                    var rec = FaaAircraftDatabase.Get(ac.AircraftType);
                    _metadata[ac.Callsign] = new AircraftMetadata
                    {
                        Callsign = ac.Callsign,
                        Type = ac.AircraftType,
                        WingspanFt = rec?.WingspanFt,
                        LengthFt = rec?.LengthFt,
                        Color = DefaultPalette[(_metadata.Count) % DefaultPalette.Length],
                    };
                }

                _ticks.Add(BuildTickEvent(elapsedSeconds, ac));
            }
        }

        public void Dispose()
        {
            _engine.TickCompleted -= _handler;
            var recording = new TickRecording
            {
                Version = SchemaVersion,
                AirportId = _airportId,
                Aircraft = _metadata.Values.OrderBy(m => m.Callsign, StringComparer.Ordinal).ToList(),
                Ticks = _ticks,
            };
            WriteJsonFile(_jsonPath, recording);
        }
    }
}

// ─── JSON DTOs ───

/// <summary>
/// Top-level structure of a TickRecorder JSON file. Schema is bumped on
/// incompatible changes (the consumer in LayoutInspector reads this same shape).
/// </summary>
public sealed class TickRecording
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("airportId")]
    public string? AirportId { get; init; }

    [JsonPropertyName("aircraft")]
    public required List<AircraftMetadata> Aircraft { get; init; }

    [JsonPropertyName("ticks")]
    public required List<TickEvent> Ticks { get; init; }
}

public sealed class AircraftMetadata
{
    [JsonPropertyName("callsign")]
    public required string Callsign { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("wingspanFt")]
    public double? WingspanFt { get; init; }

    [JsonPropertyName("lengthFt")]
    public double? LengthFt { get; init; }

    [JsonPropertyName("color")]
    public required string Color { get; init; }
}

public sealed class TickEvent
{
    [JsonPropertyName("t")]
    public required int T { get; init; }

    [JsonPropertyName("callsign")]
    public required string Callsign { get; init; }

    [JsonPropertyName("lat")]
    public required double Lat { get; init; }

    [JsonPropertyName("lon")]
    public required double Lon { get; init; }

    [JsonPropertyName("hdg")]
    public required double Hdg { get; init; }

    [JsonPropertyName("gs")]
    public required double Gs { get; init; }

    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    [JsonPropertyName("twy")]
    public string? Twy { get; init; }

    [JsonPropertyName("speedLimit")]
    public double? SpeedLimit { get; init; }

    [JsonPropertyName("nav")]
    public NavTickDto? Nav { get; init; }

    /// <summary>Indicated airspeed (kt) for airborne aircraft. Null on the ground.</summary>
    [JsonPropertyName("ias")]
    public double? Ias { get; init; }

    /// <summary>Vertical speed (fpm) for airborne aircraft. Null on the ground.</summary>
    [JsonPropertyName("vs")]
    public double? Vs { get; init; }

    /// <summary>Altitude (ft MSL) for airborne aircraft. Null on the ground.</summary>
    [JsonPropertyName("alt")]
    public double? Alt { get; init; }

    /// <summary>Phase-commanded TargetSpeed (kt). Null when the aircraft has no speed target set.</summary>
    [JsonPropertyName("tgtSpd")]
    public double? TgtSpd { get; init; }

    /// <summary>
    /// Human-readable one-line status, identical to the Aircraft List "Info" column
    /// (e.g. "Taxi to RWY 30 via D C B W W1", "Holding short 28R @ B", "Crossing runway 28L").
    /// Computed via <see cref="Yaat.Sim.AircraftStatusDescriber"/> so it matches what the operator sees.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class NavTickDto
{
    [JsonPropertyName("targetNodeId")]
    public required int TargetNodeId { get; init; }

    [JsonPropertyName("distNm")]
    public required double DistNm { get; init; }

    [JsonPropertyName("brgDeg")]
    public required double BrgDeg { get; init; }

    [JsonPropertyName("angleDiffDeg")]
    public required double AngleDiffDeg { get; init; }

    [JsonPropertyName("targetSpdKts")]
    public required double TargetSpdKts { get; init; }

    [JsonPropertyName("brakeLimitKts")]
    public required double BrakeLimitKts { get; init; }

    [JsonPropertyName("arcLimitKts")]
    public required double ArcLimitKts { get; init; }

    [JsonPropertyName("onArc")]
    public required bool OnArc { get; init; }

    [JsonPropertyName("nodeReqSpdKts")]
    public required double NodeReqSpdKts { get; init; }

    [JsonPropertyName("pathDevFt")]
    public required double PathDevFt { get; init; }

    [JsonPropertyName("segFromLat")]
    public required double SegFromLat { get; init; }

    [JsonPropertyName("segFromLon")]
    public required double SegFromLon { get; init; }

    public static NavTickDto From(NavTickDiag diag) =>
        new()
        {
            TargetNodeId = diag.TargetNodeId,
            DistNm = diag.DistToTargetNm,
            BrgDeg = diag.BearingToTargetDeg,
            AngleDiffDeg = diag.AngleDiffDeg,
            TargetSpdKts = diag.TargetSpeedKts,
            BrakeLimitKts = diag.BrakingLimitKts,
            ArcLimitKts = diag.ArcSpeedLimitKts,
            OnArc = diag.OnArc,
            NodeReqSpdKts = diag.NodeRequiredSpeedKts,
            PathDevFt = diag.PathDeviationFt,
            SegFromLat = diag.SegFromLat,
            SegFromLon = diag.SegFromLon,
        };
}

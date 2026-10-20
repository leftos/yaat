using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A radial is a magnetic bearing from the station referenced to the facility's variation of record (P/CG RADIAL; AIM
/// 1-1-17.b.5.j.1), so the OAK 090 radial lies on true bearing 090 plus OAK's station declination. JRADO intercepts and flies
/// that true radial, an <c>AT OAK090</c> condition fires on it, and an <c>AT OAK090010</c> point is projected along it.
/// </summary>
public class RadialStationVariationTests
{
    private const string Station = "OAK";
    private const int MagneticRadial = 90;
    private const int JradoFlightSeconds = 300;
    private const int JradoSettledFromSecond = 240;
    private const double ToleranceDeg = 1.0;

    private readonly ITestOutputHelper _output;

    public RadialStationVariationTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Jrado_OakRadial090_TracksTheStationsTrueRadial()
    {
        (LatLon station, double trueRadial) = StationAndTrueRadial();
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        // 2 nm out, 25 degrees short of the true radial, converging on it at 25 degrees.
        LatLon start = GeoMath.ProjectPoint(station, new TrueHeading(trueRadial - 25), 2);
        AircraftState ac = AddAirborne(engine, "RSV01", start, trueRadial + 25);
        CommandResult result = engine.SendCommand(ac.Callsign, $"JRADO {Station}{MagneticRadial:D3}");
        Assert.True(result.Success, result.Message);

        var tracks = new List<double>();
        var bearings = new List<double>();
        LatLon previous = ac.Position;
        for (int t = 1; t <= JradoFlightSeconds; t++)
        {
            engine.TickOneSecond();
            LatLon current = ac.Position;
            if (t > JradoSettledFromSecond)
            {
                tracks.Add(GeoMath.BearingTo(previous, current));
                bearings.Add(GeoMath.BearingTo(station, current));
            }

            previous = current;
        }

        _output.WriteLine(
            $"true radial {trueRadial:F2}, assigned {ac.Targets.AssignedMagneticHeading?.Degrees}, "
                + $"target true {ac.Targets.TargetTrueHeading?.Degrees:F2}, "
                + $"tracks {tracks.Min():F2}..{tracks.Max():F2}, bearings {bearings.Min():F2}..{bearings.Max():F2}"
        );
        Assert.Equal(MagneticRadial, ac.Targets.AssignedMagneticHeading?.Degrees);
        Assert.All(tracks, track => Assert.True(AngleError(track, trueRadial) <= ToleranceDeg, $"track {track:F2}, expected {trueRadial:F2}"));
        Assert.All(
            bearings,
            bearing => Assert.True(AngleError(bearing, trueRadial) <= ToleranceDeg, $"bearing from {Station} {bearing:F2}, expected {trueRadial:F2}")
        );
    }

    [Fact]
    public void AtFixRadial_FiresOnTheStationsTrueRadial()
    {
        (LatLon station, double trueRadial) = StationAndTrueRadial();
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        // Each aircraft flies outbound along its own bearing, so its bearing from the station holds for the whole run.
        AircraftState onTrueRadial = AddAirborne(engine, "RSV02", GeoMath.ProjectPoint(station, new TrueHeading(trueRadial), 10), trueRadial);
        AircraftState onRawBearing = AddAirborne(engine, "RSV03", GeoMath.ProjectPoint(station, new TrueHeading(MagneticRadial), 10), MagneticRadial);
        foreach (AircraftState ac in new[] { onTrueRadial, onRawBearing })
        {
            CommandResult result = engine.SendCommand(ac.Callsign, $"AT {Station}{MagneticRadial:D3} FH 270");
            Assert.True(result.Success, result.Message);
        }

        for (int t = 0; t < 20; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal(270, onTrueRadial.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Null(onRawBearing.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void AtFrd_ProjectsAlongTheStationsTrueRadial()
    {
        (LatLon station, double trueRadial) = StationAndTrueRadial();
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddAirborne(engine, "RSV04", GeoMath.ProjectPoint(station, new TrueHeading(0), 20), 0);
        CommandResult result = engine.SendCommand(ac.Callsign, $"AT {Station}{MagneticRadial:D3}010 FH 360");
        Assert.True(result.Success, result.Message);

        BlockTrigger trigger = Assert.Single(ac.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.ReachFrdPoint).Trigger!;
        var target = new LatLon(trigger.TargetLat!.Value, trigger.TargetLon!.Value);
        double bearing = GeoMath.BearingTo(station, target);
        _output.WriteLine($"true radial {trueRadial:F2}, target bearing {bearing:F2}, distance {GeoMath.DistanceNm(station, target):F3}");

        Assert.Equal(10, GeoMath.DistanceNm(station, target), 0.05);
        Assert.True(AngleError(bearing, trueRadial) <= 0.6, $"target bearing from {Station} {bearing:F2}, expected {trueRadial:F2}");
    }

    [Fact]
    public void AtFrd_Missed_WarnsWithTheIssuedRadial()
    {
        (LatLon station, double trueRadial) = StationAndTrueRadial();
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        // Flying east on a line that passes 3 nm north of the FRD point: outside its 1.5 nm arrival, inside the 5 nm miss window.
        LatLon frdPoint = GeoMath.ProjectPoint(station, new TrueHeading(trueRadial), 10);
        LatLon abeam = GeoMath.ProjectPoint(frdPoint, new TrueHeading(0), 3);
        AircraftState ac = AddAirborne(engine, "RSV05", GeoMath.ProjectPoint(abeam, new TrueHeading(270), 8), 90);
        CommandResult result = engine.SendCommand(ac.Callsign, $"AT {Station}{MagneticRadial:D3}010 FH 270");
        Assert.True(result.Success, result.Message);

        var warnings = new List<string>();
        engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == ac.Callsign)
            {
                warnings.Add(warning);
            }
        };
        for (int t = 0; t < 300; t++)
        {
            engine.TickOneSecond();
        }

        _output.WriteLine($"warnings: [{string.Join(" | ", warnings)}]");
        Assert.Contains(warnings, w => w.Contains($"Missed condition at {Station} R090 D010", StringComparison.Ordinal));
    }

    private static (LatLon Station, double TrueRadial) StationAndTrueRadial()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon) position = Assert.IsType<(double Lat, double Lon)>(navDb.GetFixPosition(Station));
        double declination = Assert.IsType<double>(navDb.GetStationDeclination(Station));
        Assert.True(Math.Abs(declination) > 5, $"declination {declination:F1} at {Station} is too small to tell magnetic from true");
        return (new LatLon(position.Lat, position.Lon), new TrueHeading(MagneticRadial + declination).Degrees);
    }

    private static double AngleError(double a, double b) => Math.Abs(((a - b + 540) % 360) - 180);

    private SimulationEngine? BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.GroundLayout = layout;
        engine.World.ReactionDelayRng = new SerializableRandom(42);

        // No weather timeline: calm wind, so heading and ground track agree.
        Assert.Null(engine.Scenario.WeatherTimeline);
        return engine;
    }

    private static AircraftState AddAirborne(SimulationEngine engine, string callsign, LatLon position, double trueHeading)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(trueHeading),
            Altitude = 5000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }
}

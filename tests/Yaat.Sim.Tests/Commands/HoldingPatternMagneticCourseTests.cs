using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A controller's <c>HOLDP fix course</c> gives the inbound holding course in magnetic degrees (AIM 5-3-8), so the hold is flown
/// on a true course of that course plus the hold fix's declination: the station declination when the fix is a VOR, else the
/// modelled declination at the fix. With no wind, the settled hold turns to that true inbound course and flies its outbound leg
/// on the reciprocal track.
/// </summary>
public class HoldingPatternMagneticCourseTests
{
    private const int MagneticInboundCourse = 184;
    private const int FlightSeconds = 720;
    private const int SettledAfterSeconds = 360;
    private const double ToleranceDeg = 1.0;

    private readonly ITestOutputHelper _output;

    public HoldingPatternMagneticCourseTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Holdp_AtAnRnavFix_FliesTheInboundCourseAsMagnetic_WithTheModelledDeclinationAtTheFix()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        LatLon fix = FixPosition(navDb, "SUNOL");
        Assert.Null(navDb.GetStationDeclination("SUNOL"));
        double declination = MagneticDeclination.GetDeclination(fix);

        AssertHoldFliesTrueCourse("SUNOL", fix, declination);
    }

    [Fact]
    public void Holdp_AtAVor_FliesTheInboundCourseAsMagnetic_WithTheStationDeclination()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        LatLon fix = FixPosition(navDb, "OAK");
        double declination = Assert.IsType<double>(navDb.GetStationDeclination("OAK"));

        AssertHoldFliesTrueCourse("OAK", fix, declination);
    }

    private static LatLon FixPosition(NavigationDatabase navDb, string fixName)
    {
        (double Lat, double Lon) position = Assert.IsType<(double Lat, double Lon)>(navDb.GetFixPosition(fixName));
        return new LatLon(position.Lat, position.Lon);
    }

    private void AssertHoldFliesTrueCourse(string fixName, LatLon fix, double declination)
    {
        Assert.True(Math.Abs(declination) > 5, $"declination {declination:F1} at {fixName} is too small to tell magnetic from true");
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddAirborne(engine, "HMC01", GeoMath.ProjectPoint(fix, new TrueHeading(0), 8));
        CommandResult result = engine.SendCommand(ac.Callsign, $"HOLDP {fixName} {MagneticInboundCourse} 1M R");
        Assert.True(result.Success, result.Message);
        Assert.Contains($"{MagneticInboundCourse:D3} inbound", result.Message);

        (List<double> outboundTracks, List<double> headingTargets) = FlyAndObserveHold(engine, ac, fix);
        double expectedInbound = new TrueHeading(MagneticInboundCourse + declination).Degrees;
        double expectedOutbound = new TrueHeading(expectedInbound + 180).Degrees;

        Assert.NotEmpty(outboundTracks);
        outboundTracks.Sort();
        double outboundTrack = outboundTracks[outboundTracks.Count / 2];
        _output.WriteLine(
            $"{fixName}: declination {declination:F2}, expected inbound {expectedInbound:F2}, heading targets "
                + $"[{string.Join(", ", headingTargets.Select(h => h.ToString("F1")))}], settled outbound track {outboundTrack:F2}"
        );

        Assert.True(
            AngleError(outboundTrack, expectedOutbound) <= ToleranceDeg,
            $"outbound track {outboundTrack:F2} at {fixName}, expected {expectedOutbound:F2}"
        );
        Assert.Contains(headingTargets, h => AngleError(h, expectedInbound) <= ToleranceDeg);
    }

    private static double AngleError(double a, double b) => Math.Abs(((a - b + 540) % 360) - 180);

    /// <summary>
    /// Flies the hold and, once the entry has settled, returns the ground track of every straight second flown away from the
    /// fix (the outbound leg) and each distinct heading the hold turned to while flying headings rather than to the fix.
    /// </summary>
    private static (List<double> OutboundTracks, List<double> HeadingTargets) FlyAndObserveHold(SimulationEngine engine, AircraftState ac, LatLon fix)
    {
        var outboundTracks = new List<double>();
        var headingTargets = new List<double>();
        LatLon previous = ac.Position;
        double previousTrack = double.NaN;
        for (int t = 1; t <= FlightSeconds; t++)
        {
            engine.TickOneSecond();
            Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
            LatLon current = ac.Position;
            double track = GeoMath.BearingTo(previous, current);
            bool straight = !double.IsNaN(previousTrack) && (AngleError(track, previousTrack) < 0.05);
            bool receding = GeoMath.DistanceNm(current, fix) > GeoMath.DistanceNm(previous, fix);
            bool flyingHeadings = ac.Targets.NavigationRoute.Count == 0;
            if ((t > SettledAfterSeconds) && flyingHeadings)
            {
                if (straight && receding)
                {
                    outboundTracks.Add(track);
                }

                if ((ac.Targets.TargetTrueHeading is { } target) && !headingTargets.Contains(target.Degrees))
                {
                    headingTargets.Add(target.Degrees);
                }
            }

            previous = current;
            previousTrack = track;
        }

        return (outboundTracks, headingTargets);
    }

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
        return engine;
    }

    private static AircraftState AddAirborne(SimulationEngine engine, string callsign, LatLon position)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(180),
            Altitude = 5000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }
}

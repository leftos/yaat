using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for exit-aware braking during landing rollout and exit angle/speed calculations.
/// Uses the real OAK ground layout:
/// - RWY 30 / twy W5 = high-speed exit (~30° shallow turn)
/// - RWY 28R / twy H = standard exit (~90° steep turn)
/// </summary>
public class LandingExitDecelTests
{
    private const string TestDataDir = "TestData";

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine(TestDataDir, "oak.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
    }

    private static RunwayInfo MakeRunway(string designator, double heading, double thresholdLat, double thresholdLon) =>
        TestRunwayFactory.Make(designator: designator, heading: heading, elevationFt: 9.0, thresholdLat: thresholdLat, thresholdLon: thresholdLon);

    private static AircraftState MakeLandedAircraft(double lat, double lon, double heading, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            TrueHeading = new TrueHeading(heading),
            Altitude = 9.0,
            IndicatedAirspeed = ias,
            IsOnGround = true,
            Departure = "TEST",
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, AirportGroundLayout? layout, double dt = 1.0) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = 9.0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

    private static (int ticks, double finalSpeed) SimulateRollout(LandingPhase phase, PhaseContext ctx, int maxTicks = 500)
    {
        int ticks = 0;
        while (ticks < maxTicks)
        {
            bool done = phase.OnTick(ctx);
            ticks++;
            AdvancePosition(ctx.Aircraft, ctx.DeltaSeconds);
            if (done)
            {
                break;
            }
        }

        return (ticks, ctx.Aircraft.IndicatedAirspeed);
    }

    private static void AdvancePosition(AircraftState ac, double dt)
    {
        double distNm = ac.GroundSpeed / 3600.0 * dt;
        double headingRad = ac.TrueHeading.Degrees * Math.PI / 180.0;
        ac.Latitude += distNm / 60.0 * Math.Cos(headingRad);
        ac.Longitude += distNm / (60.0 * Math.Cos(ac.Latitude * Math.PI / 180.0)) * Math.Sin(headingRad);
    }

    // -- OAK 28R: touchdown at east end (37.724806, -122.204721), heading 280° --

    [Fact]
    public void OAK28R_NoPreference_CompletesAtReasonableSpeed()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 130);
        ac.Phases!.AssignedRunway = rwy;
        var ctx = Ctx(ac, rwy, layout);

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        ac.IsOnGround = true;

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        Assert.InRange(finalSpeed, 0, 20.5);
    }

    [Fact]
    public void NoGroundLayout_FallsBackToDefault()
    {
        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 130);
        ac.Phases!.RequestedExit = new ExitPreference { Side = ExitSide.Left };

        var ctx = Ctx(ac, rwy, layout: null);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        Assert.InRange(finalSpeed, 0, 20.5);
    }

    [Fact]
    public void OAK28R_StandardExitH_CompletesAtLowerSpeed()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 130);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "H" };
        ac.Phases.AssignedRunway = rwy;

        var ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        // Standard exit (>45°) → StandardExitSpeed for jets = 15 kts
        Assert.InRange(finalSpeed, 0, 16);
    }

    [Fact]
    public void OAK30_HighSpeedExitW5_CompletesAtHigherSpeed()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        // RWY 30 touchdown at SE end, heading ~310°
        var rwy = MakeRunway("30", 310.0, 37.701486, -122.214273);
        var ac = MakeLandedAircraft(37.701486, -122.214273, 310.0, ias: 130);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "W5" };
        ac.Phases.AssignedRunway = rwy;

        var ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        // High-speed exit (≤45°) → HighSpeedExitSpeed for jets = 30 kts
        Assert.InRange(finalSpeed, 28, 31);
    }

    [Fact]
    public void OAK28R_ExitFarAhead_MaintainsCoastSpeed()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 60);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "H" };
        ac.Phases.AssignedRunway = rwy;

        var ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        for (int i = 0; i < 5; i++)
        {
            phase.OnTick(ctx);
        }

        Assert.True(ctx.Aircraft.IndicatedAirspeed >= 39.0, $"Speed dropped to {ctx.Aircraft.IndicatedAirspeed:F1}, expected >= 39");
    }

    [Fact]
    public void OAK28R_ExitPreferenceChanged_ReResolvesExit()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 80);
        ac.Phases!.AssignedRunway = rwy;

        var ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        for (int i = 0; i < 3; i++)
        {
            phase.OnTick(ctx);
        }

        double speedBefore = ac.IndicatedAirspeed;
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "H" };
        phase.OnTick(ctx);

        Assert.True(ac.IndicatedAirspeed < speedBefore);
    }

    [Fact]
    public void OAK28R_ExitBehind_FallsBackToDefault()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        // Start past P — B is behind
        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.729433, -122.219017, 280.0, ias: 80);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "B" };
        ac.Phases.AssignedRunway = rwy;

        var ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        Assert.InRange(finalSpeed, 0, 20.5);
    }

    // -- ComputeExitAngle --

    [Fact]
    public void ComputeExitAngle_OAK30_W5_IsHighSpeed()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var hsNodes = layout.GetRunwayHoldShortNodes("30");
        var w5Node = hsNodes.FirstOrDefault(n => n.Edges.Any(e => e.TaxiwayName == "W5"));
        if (w5Node is null)
        {
            return;
        }

        double? angle = layout.ComputeExitAngle(w5Node, "W5", new TrueHeading(310.0));
        Assert.NotNull(angle);
        Assert.InRange(angle.Value, 0, 45);
    }

    [Fact]
    public void ComputeExitAngle_NoMatchingTaxiway_ReturnsNull()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var hsNodes = layout.GetRunwayHoldShortNodes("28R");
        if (hsNodes.Count == 0)
        {
            return;
        }

        Assert.Null(layout.ComputeExitAngle(hsNodes[0], "NONEXISTENT", new TrueHeading(280.0)));
    }

    // -- FindExitAheadOnRunway (retained for EL/ER/EXIT commands) --

    [Fact]
    public void FindExitAhead_OAK28R_H_ReturnsIt()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var result = layout.FindExitAheadOnRunway(37.724806, -122.204721, new TrueHeading(280.0), new ExitPreference { Taxiway = "H" }, "28R");
        Assert.NotNull(result);
    }

    [Fact]
    public void FindExitAhead_NoPreference_FindsNearest()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var result = layout.FindExitAheadOnRunway(37.724806, -122.204721, new TrueHeading(280.0), null, "28R");
        Assert.NotNull(result);
    }

    [Fact]
    public void FindExitAhead_SidePreference_FiltersCorrectly()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rightResult = layout.FindExitAheadOnRunway(
            37.724806,
            -122.204721,
            new TrueHeading(280.0),
            new ExitPreference { Side = ExitSide.Right },
            "28R"
        );
        Assert.NotNull(rightResult);
    }

    // -- ExitTurnOffSpeed (pure math, no layout) --

    [Fact]
    public void ExitTurnOffSpeed_NullAngle_ReturnsFallback()
    {
        Assert.Equal(CategoryPerformance.RunwayExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, null));
    }

    [Fact]
    public void ExitTurnOffSpeed_SmallAngle_ReturnsHighSpeed()
    {
        Assert.Equal(CategoryPerformance.HighSpeedExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 30));
    }

    [Fact]
    public void ExitTurnOffSpeed_LargeAngle_ReturnsStandard()
    {
        Assert.Equal(CategoryPerformance.StandardExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 80));
    }

    [Fact]
    public void ExitTurnOffSpeed_ExactThreshold_ReturnsHighSpeed()
    {
        Assert.Equal(CategoryPerformance.HighSpeedExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 45));
    }
}

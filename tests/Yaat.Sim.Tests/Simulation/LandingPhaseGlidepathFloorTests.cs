using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Defense-in-depth regression: an aircraft that enters <see cref="LandingPhase"/> high and
/// far out must not free-fall at the category descent rate and touch down short of the
/// runway. LandingPhase relies on <see cref="FinalApproachPhase"/> having handed off on a
/// stabilized glidepath at &lt;30 ft AGL; if it instead starts far out (the failure behind the
/// 360 / S-turn-on-final bugs), nothing held it on the glidepath.
///
/// The fix clamps the pre-flare descent target to the glidepath altitude at the current
/// distance to the threshold, so the aircraft tracks the path down instead of sinking below
/// it. This test starts an aircraft directly in LandingPhase at ~2.2 nm / ~700 ft and asserts
/// it touches down on the runway, not in the undershoot.
/// </summary>
public class LandingPhaseGlidepathFloorTests(ITestOutputHelper output)
{
    private const string Callsign = "N12345";

    [Fact]
    public void LandingPhase_StartedHighAndFarOut_TouchesDownOnRunway()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "28L");
        if (rwy is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());

        // ~2.2 nm out, aligned, roughly on a 3° glidepath — but handed straight to
        // LandingPhase (no FinalApproachPhase predecessor to hold the glideslope).
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, 2.2);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = new LatLon(acLat, acLon),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 700,
            IndicatedAirspeed = 75,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(3000),
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = rwy };
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        Assert.IsType<LandingPhase>(aircraft.Phases.CurrentPhase);

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-landing-floor",
            ScenarioName = "OAK LandingPhase glidepath-floor Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        bool reachedGround = false;
        double touchdownAlongFt = 0;

        for (int t = 1; t <= 180; t++)
        {
            engine.TickOneSecond();
            double alongFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, rwy.TrueHeading) * 6076.12;
            if (aircraft.IsOnGround)
            {
                reachedGround = true;
                touchdownAlongFt = alongFt;
                output.WriteLine($"t={t}: TOUCHDOWN at {alongFt:F0} ft relative to 28L threshold (ias={aircraft.IndicatedAirspeed:F0})");
                break;
            }
            if (t % 15 == 0)
            {
                output.WriteLine($"t={t}: alt={aircraft.Altitude:F0} vs={aircraft.VerticalSpeed:F0} along={alongFt:F0}");
            }
        }

        Assert.True(reachedGround, "aircraft never touched down within the tick window");
        Assert.True(
            touchdownAlongFt >= 0,
            $"Aircraft entered LandingPhase high/far out and touched down {touchdownAlongFt:F0} ft relative to the "
                + "28L threshold — it must track the glidepath down and touch down on the runway, not short of it."
        );
    }
}

using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// End-to-end cover for re-targeting a runway exit after <see cref="RunwayExitPhase"/> has handed a route to
/// the navigator but before the turn-off begins. The unit-level gate lives in
/// <see cref="LateExitChangeTests"/>; this drives the real engine so the teardown/re-commit is exercised
/// against live geometry — the failure the original refusal guarded against was handing the pure-pursuit
/// navigator an off-centerline aircraft, which surfaces as the orbit throw armed in <c>ModuleInit</c>.
/// </summary>
public class LateExitChangeE2ETests(ITestOutputHelper output)
{
    public LateExitChangeE2ETests_Fixture Fixture { get; } = new();

    /// <summary>Pins the shared navdata/profile singletons before any test body runs (xUnit parallelizes classes).</summary>
    public sealed class LateExitChangeE2ETests_Fixture
    {
        public LateExitChangeE2ETests_Fixture() => TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void LateExitChange_BeforeTheTurnOff_RetargetsToTheNewTaxiway()
    {
        if (TestVnasData.NavigationDb is null)
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

        var runway = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(runway);

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TSTAC",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318,
            IndicatedAirspeed = 130,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-late-exit-change",
            ScenarioName = "OAK Late Exit Change",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        Assert.True(engine.SendCommand("TSTAC", "CLAND").Success);
        Assert.True(engine.SendCommand("TSTAC", "EXIT W2").Success);

        // Runway 30 exits, ordered from the threshold: W1 W2 W6 W7 W3 W4 W5. A B738 off a 1 nm final cannot
        // make W2 and relaxes to a later one, which is fine — all this test needs is a committed exit with
        // another one still ahead of it.
        const string NewExit = "W7";

        string? committedTo = null;
        bool retargeted = false;
        bool refusedAfterTurnStart = false;

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();

            if (aircraft.Phases?.CurrentPhase is not RunwayExitPhase exitPhase)
            {
                if (committedTo is not null)
                {
                    break;
                }
                continue;
            }

            if (!retargeted && !exitPhase.IsOnCenterline)
            {
                committedTo = aircraft.Ground.CurrentTaxiway;
                output.WriteLine($"t={t}: committed to {committedTo}, turnStarted={exitPhase.TurnStarted}, gs={aircraft.GroundSpeed:F1}");
                Assert.False(exitPhase.TurnStarted, "the phase latched the turn on the same tick it committed the route");

                var change = engine.SendCommand("TSTAC", $"EXIT {NewExit}");
                output.WriteLine($"t={t}: EXIT {NewExit} -> success={change.Success} msg={change.Message}");
                Assert.True(change.Success, $"late exit change was refused while still tracking the centerline: {change.Message}");
                retargeted = true;
                continue;
            }

            // Once the turn-off is actually under way the same command must be refused, leaving the aircraft
            // on the exit it is turning into.
            if (retargeted && !refusedAfterTurnStart && exitPhase.TurnStarted)
            {
                var tooLate = engine.SendCommand("TSTAC", "EXIT W5");
                output.WriteLine($"t={t}: EXIT W5 (turning) -> success={tooLate.Success} msg={tooLate.Message}");
                Assert.False(tooLate.Success, "an exit change was accepted after the turn-off had started");
                Assert.Contains("turning off", tooLate.Message!, StringComparison.OrdinalIgnoreCase);
                refusedAfterTurnStart = true;
            }
        }

        output.WriteLine($"final taxiway={aircraft.Ground.CurrentTaxiway}, phase={aircraft.Phases?.CurrentPhase?.Name}");

        Assert.True(retargeted, "aircraft never reached a committed runway exit");
        Assert.NotEqual(NewExit, committedTo);
        Assert.True(refusedAfterTurnStart, "the phase never latched the turn-off, so the refusal path went untested");
        Assert.Equal(NewExit, aircraft.Ground.CurrentTaxiway);
    }
}

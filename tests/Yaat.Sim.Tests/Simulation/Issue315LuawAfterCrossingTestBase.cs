using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The issue #315 recording and engine staging the <see cref="Issue315LuawAfterCrossingTests"/> classes share.
/// </summary>
public abstract class Issue315LuawAfterCrossingTestBase(ITestOutputHelper output)
{
    protected ITestOutputHelper Output { get; } = output;

    private protected const string RecordingPath = "TestData/issue315-luaw-after-crossing-recording.zip";

    /// <summary>Callsign for the synthetic (non-recording) crossing-exit terminus case below.</summary>
    private protected const string SyntheticCallsign = "SKW9316";

    /// <summary>
    /// Recording time at which SKW5237 is stopped on the 28R hold line, before the LUAW at t=624.
    /// Measured: the 28L crossing ends at t=619 and the taxi on to the 28R bar brakes to a stop at t=624
    /// (1.5 ft short of the planned stop point, itself 53 ft short of the painted bar) — the recorded LUAW
    /// lands in that same second and the line-up starts moving at t=625, so t=624 is the one second the
    /// aircraft is stopped and holding short. Physics owns taxi speed at 1.0 kt/s accel / 5 kt/s brake, so
    /// the arrival is 4 s later than the t=620 this used to sample.
    /// </summary>
    private protected const int Skw5237HoldingTime = 624;

    /// <summary>Recording time just past the LUAW at t=624. Ticking beyond t=666 would replay the controller's panic re-taxi.</summary>
    private protected const int Skw5237PostLuawTime = 630;

    private protected static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private protected SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("LUAW after a re-taxi crossing one parallel to the other");
        SimLogBuilder.CreateForTest(Output).InitializeSimLog();

        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    private protected HoldingShortPhase? TickUntilHoldingShort(SimulationEngine engine, string runwayDesignator, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? aircraft = engine.FindAircraft(SyntheticCallsign);
            if (aircraft is null)
            {
                return null;
            }

            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hold
                && hold.HoldShort.TargetName is { } target
                && RunwayIdentifier.Parse(target).Contains(runwayDesignator)
            )
            {
                Output.WriteLine($"t={t}: holding short of {target} at node #{hold.HoldShort.NodeId}");
                return hold;
            }
        }

        AircraftState? last = engine.FindAircraft(SyntheticCallsign);
        Output.WriteLine($"gave up after {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.Name} gs={last?.GroundSpeed:F1}");
        return null;
    }

    private protected static AircraftState SpawnOnTaxiway(GroundNode node, TrueHeading heading, AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = SyntheticCallsign,
            AircraftType = "E75L",
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "KPSC",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(37000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}

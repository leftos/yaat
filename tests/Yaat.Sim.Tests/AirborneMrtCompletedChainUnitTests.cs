using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// An airborne MRT/MLT issued after the departure phase chain has completed splices a circuit onto
/// the spent chain via <see cref="PhaseList.InsertAfterCurrent(IEnumerable{Phase})"/>. The current
/// index then points at the new UpwindPhase, which nothing has started. PhaseRunner's pending-start
/// heuristic used to treat a Pending current phase as a freshly installed list and call
/// <see cref="PhaseList.Start"/>, rewinding CurrentIndex to 0 — the aircraft re-ran its
/// already-completed ground phases mid-air (re-taxiing an old runway crossing at 12 kt at 1000 ft).
///
/// These tests pin two contracts: the MRT handler activates the spliced circuit's first phase, and
/// PhaseRunner never rewinds the index when ticking a Pending phase appended past a spent prefix.
/// </summary>
public class AirborneMrtCompletedChainUnitTests
{
    private readonly ITestOutputHelper _output;

    public AirborneMrtCompletedChainUnitTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// VFR C150 near the OAK 28R departure end at pattern altitude, carrying the spent departure
    /// chain from a completed CTO (all phases behind it, CurrentIndex past the end).
    /// </summary>
    private static AircraftState? BuildDepartedAircraftWithSpentChain()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        if (runway is null)
        {
            return null;
        }

        var aircraft = new AircraftState
        {
            Callsign = "N248ZV",
            AircraftType = "C150",
            AirportId = "OAK",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 1000,
            IndicatedAirspeed = 89,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new TaxiingPhase());
        aircraft.Phases.Add(new TakeoffPhase());
        aircraft.Phases.Add(new InitialClimbPhase());

        // Spend the chain: Clear pushes CurrentIndex past the end; stamp Completed to mirror a
        // chain that genuinely ran to completion rather than one that was skipped.
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null);
        aircraft.Phases.Clear(ctx);
        foreach (var phase in aircraft.Phases.Phases)
        {
            phase.Status = PhaseStatus.Completed;
        }

        return aircraft;
    }

    [Fact]
    public void AirborneMrt_AfterCompletedChain_ActivatesSplicedUpwind()
    {
        var aircraft = BuildDepartedAircraftWithSpentChain();
        if (aircraft is null)
        {
            return;
        }

        var result = CommandDispatcher.Dispatch(new MakeRightTrafficCommand(null, null), aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, $"MRT failed: {result.Message}");

        var current = aircraft.Phases!.CurrentPhase;
        _output.WriteLine($"after MRT: index={aircraft.Phases.CurrentIndex} phase={current?.GetType().Name} status={current?.Status}");
        var upwind = Assert.IsType<UpwindPhase>(current);
        Assert.Equal(PhaseStatus.Active, upwind.Status);
    }

    [Fact]
    public void PhaseRunner_PendingPhaseAppendedPastSpentChain_NeverRewindsIndex()
    {
        var aircraft = BuildDepartedAircraftWithSpentChain();
        if (aircraft is null)
        {
            return;
        }

        CommandDispatcher.Dispatch(new MakeRightTrafficCommand(null, null), aircraft, TestDispatch.Context(Random.Shared));
        int indexAfterMrt = aircraft.Phases!.CurrentIndex;
        Assert.IsType<UpwindPhase>(aircraft.Phases.CurrentPhase);

        // Regardless of whether the handler pre-activated the spliced phase, PhaseRunner must
        // never rewind into the spent prefix on a Pending current phase.
        aircraft.Phases.CurrentPhase!.Status = PhaseStatus.Pending;
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null);
        PhaseRunner.Tick(aircraft, ctx);

        var current = aircraft.Phases.CurrentPhase;
        _output.WriteLine($"after tick: index={aircraft.Phases.CurrentIndex} phase={current?.GetType().Name} status={current?.Status}");
        Assert.True(
            aircraft.Phases.CurrentIndex >= indexAfterMrt,
            $"PhaseRunner rewound CurrentIndex from {indexAfterMrt} to {aircraft.Phases.CurrentIndex} "
                + $"({current?.GetType().Name}) — the spent prefix must never re-run."
        );
        Assert.True(
            current is UpwindPhase or CrosswindPhase,
            $"Expected the spliced circuit to be flying (Upwind/Crosswind), got {current?.GetType().Name}."
        );
    }
}

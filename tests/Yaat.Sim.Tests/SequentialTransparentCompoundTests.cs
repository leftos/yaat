using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #407: a `;`-sequenced compound whose FIRST block is a lone phase-transparent command
/// (<c>SQ; SQNORM; PUSH; TAXI B5 HS B</c> at the gate) was rejected outright — DispatchWithPhase
/// gates only Blocks[0], and FindPhaseGateDriverIndex's "unreachable" fallback made the
/// transparent SQ the phase-gate driver, which AtParkingPhase rejects ("aircraft is parked with
/// engines off"). Transparent commands must never drive the gate: leading all-transparent
/// blocks peel off and apply immediately, and the gate is driven by the first block containing a
/// phase-interactive command. The parallel-block form (<c>SQ, SQNORM, PUSH</c>) already worked —
/// see <see cref="PhaseTransparentCommandTests"/>.
/// </summary>
public class SequentialTransparentCompoundTests
{
    public SequentialTransparentCompoundTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraftAtParking()
    {
        var ac = new AircraftState
        {
            Callsign = "FDX440",
            AircraftType = "B763",
            Position = new LatLon(37.7184, -122.2187),
            TrueHeading = new TrueHeading(297),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Transponder = new AircraftTransponder
            {
                AssignedCode = 4611,
                Code = 7654,
                Mode = "Standby",
            },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    private static CommandResult Dispatch(AircraftState ac, string input)
    {
        var parsed = CommandParser.ParseCompound(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));
    }

    [Fact]
    public void TransparentLeadingBlocks_AtParking_Succeeds()
    {
        var ac = MakeAircraftAtParking();

        var result = Dispatch(ac, "SQ; SQNORM; PUSH; TAXI B5 HS B");

        Assert.True(result.Success, result.Message);
        Assert.Equal(4611u, ac.Transponder.Code);
        Assert.Equal("C", ac.Transponder.Mode);
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        // The TAXI must be queued untriggered behind the pushback.
        Assert.Contains(
            ac.Queue.Blocks,
            b => !b.IsApplied && b.Trigger is null && (b.Description ?? "").Contains("TAXI", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void TransparentLeadingBlocks_ReadbackComposesSequentially()
    {
        var ac = MakeAircraftAtParking();

        var result = Dispatch(ac, "SQ; SQNORM; PUSH");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Message);
        Assert.Contains(" ; then ", result.Message);
        Assert.Contains("Pushing back", result.Message);
    }

    [Fact]
    public void TransparentLeading_DriverRejected_TransparentsApplied_FailureSurfaces()
    {
        var ac = MakeAircraftAtParking();

        // CTO is rejected at parking; sequential semantics mean the already-peeled SQ stays applied.
        var result = Dispatch(ac, "SQ; CTO");

        Assert.False(result.Success);
        Assert.Equal(4611u, ac.Transponder.Code);
        // The failure message names what already applied before the rejection.
        Assert.StartsWith("Squawk 4611; but ", result.Message ?? "");
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void TransparentLeading_NoPhase_BothApply()
    {
        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(90),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan(),
            Transponder = new AircraftTransponder { AssignedCode = 4611, Code = 7654 },
        };

        var result = Dispatch(ac, "SQ; FH 270");

        Assert.True(result.Success, result.Message);
        Assert.Equal(4611u, ac.Transponder.Code);
        Assert.NotNull(ac.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void TransparentLeading_ThenWait_Defers()
    {
        var ac = MakeAircraftAtParking();

        var result = Dispatch(ac, "SQ; WAIT 5 PUSH");

        Assert.True(result.Success, result.Message);
        Assert.Equal(4611u, ac.Transponder.Code);
        // The WAIT-led remainder becomes a deferred dispatch, not a queued block or an immediate PUSH.
        Assert.Single(ac.DeferredDispatches);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
    }
}

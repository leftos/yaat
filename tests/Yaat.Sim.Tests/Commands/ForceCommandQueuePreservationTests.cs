using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Force-altitude / force-speed (CMN/DMN, SPDN) are per-axis overrides: a forced altitude touches only the
/// vertical axis and a forced speed only the speed axis, exactly like their non-forced counterparts CM/DM and
/// SPD. The dimension contract (command-handlers.md "Incoming vs. queued is a different question") says an
/// altitude or speed assignment issued on the way to a fix must NOT cancel a queued, condition-waiting pattern
/// entry (a lateral plan). The non-forced CM honours this; the forced CMN must too.
///
/// Before the fix, ForceHeading/ForceAltitude/ForceSpeed classified to TrackedCommandType.Immediate →
/// CommandDimension.None, and were absent from the broad IsPhaseTransparent list, so a Force* command to a
/// phase-less aircraft fell through to the None-dimension ClearConflictingBlocks fast path that wipes the
/// ENTIRE queue — the same N435C/S2-OAK-5 regression class the transparent set was widened to fix.
/// </summary>
public class ForceCommandQueuePreservationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public ForceCommandQueuePreservationTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    private static AircraftState MakeAirborneVfr() =>
        new()
        {
            Callsign = "N435C",
            AircraftType = "M20P",
            Position = new LatLon(37.62, -122.19),
            TrueHeading = new TrueHeading(340),
            Altitude = 1500,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KOAK",
            },
        };

    private void DispatchOk(AircraftState ac, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(result.Success, result.Message);
        _output.WriteLine($"=== After {text} (queue {ac.Queue.Blocks.Count} blocks) ===");
        for (int i = 0; i < ac.Queue.Blocks.Count; i++)
        {
            CommandBlock b = ac.Queue.Blocks[i];
            _output.WriteLine($"  [{i}] applied={b.IsApplied} desc='{b.NaturalDescription}'");
        }
    }

    [Fact]
    public void ForceAltitude_WithNullPhases_PreservesQueuedErd()
    {
        AircraftState ac = MakeAirborneVfr();
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false);

        DispatchOk(ac, "DCT OAK; ERD 28R", ctx);
        Assert.Null(ac.Phases?.CurrentPhase); // ERD waits for DCT to complete; no active phase yet
        Assert.Equal(2, ac.Queue.Blocks.Count);

        // A forced altitude touches only the vertical axis; the queued lateral pattern entry must survive.
        DispatchOk(ac, "CMN 50", ctx);

        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Enter right downwind"));
    }

    [Fact]
    public void ForceSpeed_WithNullPhases_PreservesQueuedErd()
    {
        AircraftState ac = MakeAirborneVfr();
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false);

        DispatchOk(ac, "DCT OAK; ERD 28R", ctx);
        Assert.Equal(2, ac.Queue.Blocks.Count);

        // A forced speed touches only the speed axis; the queued lateral pattern entry must survive.
        DispatchOk(ac, "SPDN 250", ctx);

        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Enter right downwind"));
    }

    [Fact]
    public void NonForcedAltitude_WithNullPhases_PreservesQueuedErd()
    {
        // Control: the non-forced CM already preserves the queued entry. The forced CMN must match it.
        AircraftState ac = MakeAirborneVfr();
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false);

        DispatchOk(ac, "DCT OAK; ERD 28R", ctx);
        Assert.Equal(2, ac.Queue.Blocks.Count);

        DispatchOk(ac, "CM 50", ctx);

        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Enter right downwind"));
    }
}

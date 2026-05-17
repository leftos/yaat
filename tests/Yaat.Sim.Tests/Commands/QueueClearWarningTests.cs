using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// When a command's dispatch removes pending blocks from the command queue
/// (via the dimension-aware <c>ClearConflictingBlocks</c> path or the fast-
/// path All-clear), a warning must surface naming what was dropped and which
/// command did it. Goal: give the RPO visibility into silent queue churn so
/// they can spot commands that mistakenly wipe queued instructions (the same
/// failure class that hid the RTIS-wipes-ERD bug for so long).
/// </summary>
public class QueueClearWarningTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public QueueClearWarningTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    private static AircraftState MakeAirborne() =>
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

    private static DispatchContext Ctx() => TestDispatch.Context(Random.Shared, validateDctFixes: false);

    private static void DispatchOk(AircraftState ac, string text)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx());
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void ConflictingLateral_DropsPendingNavigation_EmitsWarning()
    {
        var ac = MakeAirborne();

        // Queue two lateral blocks: DCT OAK (applied/current) and DCT VPCOL (pending).
        // Then FH 270 — Lateral, conflicts with the pending DCT VPCOL block → dropped.
        // (DCT OAK as the current applied block is marked complete in-place, no warning.)
        DispatchOk(ac, "DCT OAK; DCT VPCOL");
        Assert.Equal(2, ac.Queue.Blocks.Count);
        ac.PendingWarnings.Clear();

        DispatchOk(ac, "FH 270");

        Assert.NotEmpty(ac.PendingWarnings);
        var warning = ac.PendingWarnings.First();
        _output.WriteLine($"Warning: {warning}");
        Assert.Contains("N435C", warning);
        Assert.Contains("queue cleared", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FH 270", warning);
        Assert.Contains("VPCOL", warning);
    }

    [Fact]
    public void NonConflictingAltitude_PreservesNavigationBlock_NoWarning()
    {
        var ac = MakeAirborne();

        // DCT OAK queues a lateral block. CM 050 is altitude-only — no dimensional
        // overlap, so the lateral block is preserved and no warning fires.
        DispatchOk(ac, "DCT OAK");
        Assert.Single(ac.Queue.Blocks);
        ac.PendingWarnings.Clear();

        DispatchOk(ac, "CM 050");

        Assert.Empty(ac.PendingWarnings);
        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("OAK"));
    }

    [Fact]
    public void EmptyQueue_NoWarning()
    {
        var ac = MakeAirborne();
        Assert.Empty(ac.Queue.Blocks);

        DispatchOk(ac, "FH 270");

        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void IdenticalResend_DoesNotEmitLostWarning()
    {
        // Issue #154 #5: re-sending the same compound used to emit a misleading
        // "lost: DCT VPCOL, ERD 28R" warning even though the resend was about to
        // re-enqueue those same blocks. The warning should suppress drops whose
        // canonical form is present in the incoming compound.
        var ac = MakeAirborne();
        DispatchOk(ac, "DCT OAK; DCT VPCOL; ERD 28R");
        Assert.Equal(3, ac.Queue.Blocks.Count);
        ac.PendingWarnings.Clear();

        DispatchOk(ac, "DCT OAK; DCT VPCOL; ERD 28R");

        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("queue cleared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PartialResend_OnlyWarnsAboutTrulyLostBlocks()
    {
        // Re-sending a compound that overlaps the previous queue but loses one
        // block (because the new compound doesn't include it) should warn only
        // for the truly-lost block, not for the blocks being re-enqueued.
        var ac = MakeAirborne();
        DispatchOk(ac, "DCT OAK; DCT VPCOL; FH 270");
        ac.PendingWarnings.Clear();

        // New compound drops FH 270 but keeps DCT OAK + DCT VPCOL.
        DispatchOk(ac, "DCT OAK; DCT VPCOL");

        var warning = Assert.Single(ac.PendingWarnings);
        // Warning format: "<callsign> queue cleared by <src> (lost: <items>)".
        // Only the "(lost: …)" tail should be checked for VPCOL — the <src> echo
        // of the new compound naturally repeats it.
        var lostSegment = warning[warning.IndexOf("(lost:", StringComparison.Ordinal)..];
        Assert.Contains("FH 270", lostSegment);
        Assert.DoesNotContain("VPCOL", lostSegment);
    }

    [Fact]
    public void TransparentCommand_PreservesQueue_NoQueueWarning()
    {
        var ac = MakeAirborne();
        DispatchOk(ac, "DCT OAK; ERD 28R");
        Assert.Equal(2, ac.Queue.Blocks.Count);
        ac.PendingWarnings.Clear();

        DispatchOk(ac, "RFIS");

        // Transparent path skips ClearConflictingBlocks entirely → no queue warning.
        // RFIS still emits its own "field in sight" PendingWarning (pilot readback);
        // we only assert no "queue cleared" warning fires.
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("queue cleared", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Enter right downwind"));
    }
}

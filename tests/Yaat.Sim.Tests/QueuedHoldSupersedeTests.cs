using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// A queued (not-yet-fired) VFR hold — HPPL/HPPR/HPP and the HFIX* fix holds — occupies the
/// lateral axis while it waits, exactly like a queued pattern entry or approach clearance. A
/// fresh vector must therefore cancel it (the controller changed the plan before the hold fired).
///
/// Regression: <c>CommandDescriber.GetQueuedCommandDimension</c> special-cased pattern entries and
/// approach clearances to Lateral but left the four VFR hold commands to fall through to
/// <c>ClassifyCommand</c>, which has no hold arm → <c>Immediate</c> → dimension <c>None</c>. Meanwhile
/// <c>GetCommandDimension</c> classifies those same holds as Lateral, so the queued block's aggregate
/// <c>Dimensions</c> reported a lateral conflict while its per-command keep-test read None. In
/// <c>SplitBlockNonConflicting</c> that means every command index is "kept" and the whole block
/// survives the supersede — the exact "per-command dims all None while the block reports a conflict
/// in aggregate" anti-pattern the command-handlers doc warns about (the RELR-20 shape).
/// </summary>
public class QueuedHoldSupersedeTests
{
    public QueuedHoldSupersedeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft()
    {
        return new AircraftState
        {
            Callsign = "N929AW",
            AircraftType = "BE33",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            TrueTrack = new TrueHeading(090),
            Altitude = 3000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
        };
    }

    private static bool HasQueuedHold(AircraftState ac) =>
        ac.Queue.Blocks.Any(b => (b.ParsedCommands ?? []).Any(c => c is HoldPresentPosition360Command));

    [Fact]
    public void FreshVector_CancelsQueuedHoldOrbit()
    {
        var ac = MakeAircraft();

        // Queue a conditional hold: when the aircraft reaches 5000 ft, orbit right in place.
        // (Altitude condition keeps the repro free of any nav-fix lookup.)
        var holdCompound = CommandParser.ParseCompound("AT 5000 HPPR");
        Assert.True(holdCompound.IsSuccess, $"Hold parse failed: {holdCompound.Reason}");

        var holdResult = CommandDispatcher.DispatchCompound(holdCompound.Value!, ac, TestDispatch.Context(Random.Shared, validateDctFixes: false));
        Assert.True(holdResult.Success, $"Hold dispatch failed: {holdResult.Message}");
        Assert.True(HasQueuedHold(ac), "Precondition: the conditional hold should be sitting in the queue.");

        // Controller changes the plan before the hold fires: a fresh lateral vector.
        var vectorCompound = CommandParser.ParseCompound("FH 270");
        Assert.True(vectorCompound.IsSuccess, $"Vector parse failed: {vectorCompound.Reason}");

        var vectorResult = CommandDispatcher.DispatchCompound(vectorCompound.Value!, ac, TestDispatch.Context(Random.Shared, validateDctFixes: false));
        Assert.True(vectorResult.Success, $"Vector dispatch failed: {vectorResult.Message}");

        // The lateral vector must supersede the queued lateral hold — otherwise, once the aircraft
        // passes 5000 ft it turns itself back into the orbit the controller just vectored it out of.
        Assert.False(HasQueuedHold(ac), "The fresh FH vector should have cancelled the queued hold orbit, but it survived in the queue.");
    }
}

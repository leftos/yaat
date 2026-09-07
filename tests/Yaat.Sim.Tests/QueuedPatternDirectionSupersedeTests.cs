using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// A queued (not-yet-fired) pattern-direction command — MLT/MRT (make left/right traffic) — occupies
/// the lateral axis while it waits, exactly like a queued pattern entry, approach clearance, or VFR
/// hold. A fresh vector must therefore cancel it (the controller changed the plan before the pattern
/// command fired) — otherwise, once the trigger meets, the aircraft turns itself into the pattern the
/// vector was meant to keep it out of. When MLT/MRT fires on an airborne aircraft it builds and
/// activates a full closed-traffic circuit (<c>PatternCommandHandler.TryChangePatternDirection</c>,
/// the "departure told to stay in closed traffic" path), so its survival is a real turn, not a no-op.
///
/// Regression (same class as the #336 hold fix, unfixed for the pattern-modifier family):
/// <c>CommandDescriber.GetQueuedCommandDimension</c> special-cases pattern entries, approach
/// clearances, and the four VFR holds to Lateral, but MLT/MRT (and the other pattern-modifier tower
/// verbs) have no arm in <c>ClassifyCommand</c> → <c>Immediate</c> → dimension <c>None</c>. Meanwhile
/// <c>GetCommandDimension</c> classifies them as a tower command → <c>All</c>. So the queued block's
/// aggregate <c>Dimensions</c> reports a conflict while its per-command keep-test reads None — in
/// <c>SplitBlockNonConflicting</c> every command index is "kept" and the whole block survives the
/// supersede. That is the exact "per-command dims all None while the block reports a conflict in
/// aggregate" anti-pattern the command-handlers doc warns about (the RELR-20 shape).
/// </summary>
public class QueuedPatternDirectionSupersedeTests
{
    public QueuedPatternDirectionSupersedeTests()
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

    private static bool HasQueuedMakeTraffic(AircraftState ac) =>
        ac.Queue.Blocks.Any(b => (b.ParsedCommands ?? []).Any(c => c is MakeLeftTrafficCommand or MakeRightTrafficCommand));

    [Fact]
    public void FreshVector_CancelsQueuedMakeTraffic()
    {
        var ac = MakeAircraft();

        // Queue a conditional pattern-direction command: when the aircraft reaches 5000 ft, make left
        // traffic. (Altitude condition keeps the repro free of any nav-fix lookup.)
        var mltCompound = CommandParser.ParseCompound("AT 5000 MLT");
        Assert.True(mltCompound.IsSuccess, $"MLT parse failed: {mltCompound.Reason}");

        var mltResult = CommandDispatcher.DispatchCompound(mltCompound.Value!, ac, TestDispatch.Context(Random.Shared, validateDctFixes: false));
        Assert.True(mltResult.Success, $"MLT dispatch failed: {mltResult.Message}");
        Assert.True(HasQueuedMakeTraffic(ac), "Precondition: the conditional MLT should be sitting in the queue.");

        // Controller changes the plan before the pattern command fires: a fresh lateral vector.
        var vectorCompound = CommandParser.ParseCompound("FH 270");
        Assert.True(vectorCompound.IsSuccess, $"Vector parse failed: {vectorCompound.Reason}");

        var vectorResult = CommandDispatcher.DispatchCompound(
            vectorCompound.Value!,
            ac,
            TestDispatch.Context(Random.Shared, validateDctFixes: false)
        );
        Assert.True(vectorResult.Success, $"Vector dispatch failed: {vectorResult.Message}");

        // The lateral vector must supersede the queued lateral pattern-direction command — otherwise,
        // once the aircraft passes 5000 ft it turns itself into the pattern the controller just
        // vectored it out of.
        Assert.False(HasQueuedMakeTraffic(ac), "The fresh FH vector should have cancelled the queued MLT, but it survived in the queue.");
    }
}

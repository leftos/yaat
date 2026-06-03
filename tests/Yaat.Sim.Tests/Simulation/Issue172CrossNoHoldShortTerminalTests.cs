using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 W4 — regression coverage for the existing "clear the runway and hold just past it"
/// capability, and the positive counterpart to the JBU577 spin (W1): when an aircraft crosses a runway
/// and <b>no binding taxiway hold-short</b> sits within a fuselage length past the far bars, it must
/// clear the runway and hold just past — the <see cref="CrossingRunwayPhase"/> ½-length tail-clearance
/// append fires (W1's Fix B suppresses that append only when a binding taxiway hold-short is present;
/// here there is none, so it must NOT be suppressed). The aircraft must not stop short with its tail
/// over the bars, and must not reverse.
///
/// Vehicle: OAK recording 4d4344011a72.zip (N427MX lands 28L, exits north onto taxiway G, is cleared
/// <c>TAXI G C HS 28R</c> then <c>RES</c> to cross 28R). G crosses 28R at 90° (entry-side hold-short
/// 503 / far-side hold-short 361), with the G/C junction (#350) just past the far bars — so a cleared
/// aircraft settles just past 361, near #350. This replays the recorded crossing faithfully and locks
/// the W4 property the sibling <see cref="OakCrossThenHoldOnNextTaxiwayTests"/> does not assert: the
/// final hold position is past the far-side bars (runway cleared), not short of them.
/// </summary>
public class Issue172CrossNoHoldShortTerminalTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";
    private const string Callsign = "N427MX";

    // G's 28R hold-shorts and the junction just past the far side (verified via LayoutInspector).
    private const int EntryHoldShortNode = 503; // south approach side (28L side) — where it holds short
    private const int FarHoldShortNode = 361; // north exit side — the far bars it must clear
    private const int GcJunctionNode = 350; // first junction past the far bars

    private const int ResCommandTime = 1293; // recorded RES that clears the 28R crossing
    private const int SettleTime = 1380; // ~87s past RES — well past the crossing and roll-out to the stop

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void CrossingWithNoBindingTaxiwayHoldShort_ClearsRunwayAndHoldsJustPast_WithoutStoppingShort()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var entryHs = layout.Nodes[EntryHoldShortNode];
        var farHs = layout.Nodes[FarHoldShortNode];
        var gcJunction = layout.Nodes[GcJunctionNode];

        // Replay the recorded crossing faithfully (recorded TAXI G C HS 28R at t=1243, RES at t=1293).
        engine.Replay(recording, 1244);

        bool sawHoldingShort28R = false;
        bool sawCrossing = false;
        bool stoppedShortOfFarBars = false; // W1 anti-pattern: halted on the runway side of the far bars
        double maxRetreatTowardRunway = 0; // backward motion toward the runway after clearing the far bars
        double lastDistEntry = double.NaN; // distance from the entry-side bars — grows as it leaves the runway

        for (int t = 1245; t <= SettleTime; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            var phase = ac.Phases?.CurrentPhase;
            if (phase is HoldingShortPhase hs && (hs.HoldShort.TargetName?.Contains("28R", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                sawHoldingShort28R = true;
            }

            if (phase is CrossingRunwayPhase)
            {
                sawCrossing = true;
            }

            double distEntry = GeoMath.DistanceNm(ac.Position, entryHs.Position) * 6076.12;
            double distFar = GeoMath.DistanceNm(ac.Position, farHs.Position) * 6076.12;

            // "Past the far bars" = closer to the far-side hold-short than to the entry-side one.
            bool pastFarBars = distFar < distEntry;

            // Once it is past the far bars, leaving the runway grows distEntry monotonically; any
            // decrease means it turned back toward the runway (the JBU577 spin signature).
            if (pastFarBars && !double.IsNaN(lastDistEntry))
            {
                maxRetreatTowardRunway = Math.Max(maxRetreatTowardRunway, lastDistEntry - distEntry);
            }
            lastDistEntry = pastFarBars ? distEntry : double.NaN;

            // A stopped aircraft (in a hold phase) that is still short of the far bars would be the
            // JBU577 failure mode — tail over the runway. The far bars are cleared here, so this must
            // never trigger after the crossing.
            if (sawCrossing && ac.IndicatedAirspeed < 0.5 && phase is HoldingShortPhase && !pastFarBars)
            {
                stoppedShortOfFarBars = true;
            }

            if (t == ResCommandTime || t >= SettleTime - 1 || t % 15 == 0)
            {
                NearestNodeHelper.Log(
                    output,
                    $"t={t} phase={phase?.GetType().Name ?? "null"} ias={ac.IndicatedAirspeed:F1} distEntry={distEntry:F0} distFar={distFar:F0}",
                    ac,
                    layout
                );
            }
        }

        var final = engine.FindAircraft(Callsign);
        Assert.NotNull(final);
        var finalPhase = final.Phases?.CurrentPhase;
        double finalDistEntry = GeoMath.DistanceNm(final.Position, entryHs.Position) * 6076.12;
        double finalDistFar = GeoMath.DistanceNm(final.Position, farHs.Position) * 6076.12;
        double finalDistJct = GeoMath.DistanceNm(final.Position, gcJunction.Position) * 6076.12;
        output.WriteLine(
            $"FINAL phase={finalPhase?.GetType().Name ?? "null"} ias={final.IndicatedAirspeed:F1} "
                + $"distEntry={finalDistEntry:F0} distFar={finalDistFar:F0} distJct={finalDistJct:F0} maxRetreat={maxRetreatTowardRunway:F0}"
        );

        // The capability chain: hold short of 28R, then cross it via CrossingRunwayPhase.
        Assert.True(sawHoldingShort28R, "expected HoldingShortPhase of 28R before RES");
        Assert.True(sawCrossing, "expected CrossingRunwayPhase to track the 28R crossing after RES");

        // Cleared the runway and came to rest just past it.
        Assert.True(
            finalPhase is HoldingInPositionPhase,
            $"expected HoldingInPositionPhase after clearing 28R; got {finalPhase?.GetType().Name ?? "null"}"
        );
        Assert.True(final.IndicatedAirspeed < 0.5, $"aircraft should be stopped; ias={final.IndicatedAirspeed:F1}");

        // W4 / Fix-B non-regression: it CLEARED the far bars (½-length tail-clearance append fired —
        // not suppressed, since no binding taxiway hold-short is present) rather than stopping short.
        Assert.True(
            finalDistFar < finalDistEntry,
            $"aircraft should rest past the far-side bars (distFar={finalDistFar:F0} < distEntry={finalDistEntry:F0})"
        );
        Assert.False(
            stoppedShortOfFarBars,
            "aircraft must not halt in a hold short of the far bars with its tail over the runway (the JBU577 failure mode)"
        );

        // Holds just past — near the first junction, not walked far down the field.
        Assert.True(finalDistJct < 250, $"aircraft should hold just past 28R near the G/C junction; was {finalDistJct:F0} ft from #350");

        // No reversal after clearing the runway (the JBU577 spin signature).
        Assert.True(
            maxRetreatTowardRunway < 50,
            $"aircraft must not back up toward the runway after clearing it; max retreat was {maxRetreatTowardRunway:F0} ft"
        );
    }
}

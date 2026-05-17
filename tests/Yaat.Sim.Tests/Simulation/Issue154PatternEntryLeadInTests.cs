using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #154 #2: bug report claimed N775JW (10 nm north of OAK, descending through
/// 2 354 ft heading SSE 155°) flew "NW toward REBAS" after ERD 28R. The replay shows
/// the opposite — the PTN-LEADIN waypoint installed at (37.7459, -122.2280) is 2.5 nm
/// south-east of the aircraft at dispatch, and the aircraft tracked directly to it.
///
/// This test pins that behaviour against the recorded bundle so a future change to
/// `PatternCommandHandler.ChooseDownwindLeadIn` can't regress the geometry without
/// the failure being obvious. If a real "circles wide to the NW" case shows up in
/// the future, capture a fresh bundle and add a sibling test against that one.
/// </summary>
public class Issue154PatternEntryLeadInTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue154-vfr-cold-calls-recording.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void N775JW_ErdDispatch_LeadInIsSouthOfAircraft()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // ERD 28R fires at t=553. Replay to t=555 (just past dispatch) and snapshot
        // the installed pattern-entry route.
        engine.Replay(recording, 555);

        var ac = engine.FindAircraft("N775JW");
        Assert.NotNull(ac);
        Assert.NotEmpty(ac.Targets.NavigationRoute);

        var leadIn = ac.Targets.NavigationRoute[0];
        Assert.Equal("PTN-LEADIN", leadIn.Name);

        // Aircraft was at 37.7820, -122.2491 heading 155° (SSE) at t=555.
        // PTN-LEADIN at 37.7459, -122.2280 is south-east of that — bearing should be
        // in the southern hemisphere (90°–270°), not NW toward REBAS (37.94, -122.38).
        double bearing = GeoMath.BearingTo(ac.Position, leadIn.Position);
        output.WriteLine($"Aircraft {ac.Position.Lat:F4},{ac.Position.Lon:F4} hdg {ac.TrueHeading.Degrees:F1}");
        output.WriteLine($"PTN-LEADIN {leadIn.Position.Lat:F4},{leadIn.Position.Lon:F4} bearing {bearing:F1}");

        Assert.InRange(bearing, 90.0, 270.0);

        // And the LEADIN should be reasonably close — not a 1.5 nm extension on a
        // 10 nm-out aircraft. Cap at 4 nm so the test catches a regression where
        // the lead-in distance scales with aircraft distance.
        double distNm = GeoMath.DistanceNm(ac.Position, leadIn.Position);
        output.WriteLine($"LEADIN distance: {distNm:F2} nm");
        Assert.True(distNm < 4.0, $"PTN-LEADIN should sit within ~4 nm of the aircraft, got {distNm:F2}");
    }

    /// <summary>
    /// The recording ends 1 second into N775JW's auto-go-around upwind leg (the
    /// aircraft was never cleared to land, so a GoAround phase chained on at
    /// t≈825 and Upwind started at t≈855). The user observed the aircraft
    /// "heading NW toward REBAS" — that's the upwind climb-out on runway 28R
    /// (heading 280°-292°), which happens to point toward REBAS (37.94, -122.38).
    /// Standard pattern flying — the aircraft must climb to within 300 ft of
    /// pattern altitude before turning crosswind.
    ///
    /// This test continues the simulation past the recording end and confirms
    /// the crosswind turn happens within a sane time/distance window so a
    /// future regression that makes upwind run forever (e.g. minTurnAltitude
    /// drifts up, or PatternAltitude is mis-computed) fails loudly.
    /// </summary>
    [Fact]
    public void N775JW_GoAroundUpwind_TurnsCrosswindWithinReasonableTime()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to end of recording (t=856), aircraft 1 s into Upwind on 28R.
        engine.Replay(recording, 856);
        var ac = engine.FindAircraft("N775JW");
        Assert.NotNull(ac);
        Assert.IsType<UpwindPhase>(ac.Phases?.CurrentPhase);
        output.WriteLine($"start: t=856 alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} pos={ac.Position.Lat:F4},{ac.Position.Lon:F4}");

        // Tick forward at most 240 s (a piston climbing 600 fpm from ~800 ft to
        // pattern altitude ~2 000 ft needs ~120 s; 240 s gives plenty of margin
        // before declaring "upwind ran too long"). Confirm the phase advanced to
        // Crosswind and the heading turned through ~90°.
        int seenCrosswindAt = -1;
        for (int dt = 1; dt <= 240; dt++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N775JW");
            if (ac is null)
            {
                break;
            }

            if (ac.Phases?.CurrentPhase is CrosswindPhase)
            {
                seenCrosswindAt = dt;
                output.WriteLine($"crosswind reached at t=856+{dt} alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0}");
                break;
            }
        }

        Assert.True(seenCrosswindAt > 0, $"Upwind never advanced to Crosswind within 240 s. Final phase: {ac?.Phases?.CurrentPhase?.GetType().Name}");
        Assert.True(seenCrosswindAt <= 180, $"Upwind took too long to turn crosswind ({seenCrosswindAt} s) — pattern feels distant.");
    }
}

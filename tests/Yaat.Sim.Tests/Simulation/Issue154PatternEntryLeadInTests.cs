using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
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
}

using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Situation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Situation;

/// <summary>
/// Two VFR arrivals to KOAK from KKOA in S2-OAK-3 (VFR Sequencing), N929AW and N2BP, both holding
/// <c>DCT VPCBT; ERB 28R</c> at the report (t=1217, paused), are both VFR arrival, inbound. N2BP, turned away 1.2 NM north
/// of the field (<c>TL 360</c> at t=1125) and sent east to set up the right base, is 3.0 NM out and 154° off the bearing
/// to the field. The aircraft are restored from the bundle's snapshots and classified directly, since a replay on today's
/// sim flies N2BP elsewhere.
/// </summary>
public sealed class Yaat2253VfrArrivalTurnedAwayTests
{
    private const string RecordingPath = "TestData/yaat2253-vfr-arrival-menus-recording.yaat-bug-report-bundle.zip";

    /// <summary>The report: both aircraft hold <c>DCT VPCBT; ERB 28R</c> and the sim is paused.</summary>
    private const double ReportSeconds = 1217;

    /// <summary>N2BP 1.17 NM from KOAK and 98° off the bearing to it, before any pattern entry is queued.</summary>
    private const double TurnedAwaySeconds = 1150;

    /// <summary>The snapshot before it, where N2BP is stored as inbound.</summary>
    private const double StillInboundSeconds = 1145;

    public Yaat2253VfrArrivalTurnedAwayTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void AtTheReport_BothVfrArrivalsAreInbound()
    {
        AircraftState n929aw = Restore(ReportSeconds, "N929AW");
        AircraftState n2bp = Restore(ReportSeconds, "N2BP");

        Assert.Equal(AircraftSituation.VfrArrivalInbound, SituationClassifier.Classify(n929aw, ReportSeconds, AircraftSituation.VfrArrivalInbound));
        Assert.Equal(AircraftSituation.VfrArrivalInbound, SituationClassifier.Classify(n2bp, ReportSeconds, AircraftSituation.VfrArrivalInbound));
    }

    [Fact]
    public void TurnedAwayInsideTheInboundRadius_StaysInbound()
    {
        AircraftState before = Restore(StillInboundSeconds, "N2BP");
        AircraftState after = Restore(TurnedAwaySeconds, "N2BP");

        Assert.Equal(AircraftSituation.VfrArrivalInbound, before.Situation.Current);
        Assert.Equal(AircraftSituation.VfrArrivalInbound, SituationClassifier.Classify(after, TurnedAwaySeconds, before.Situation.Current));
    }

    [Fact]
    public void WithAQueuedPatternEntry_IsInboundWhateverWasStored()
    {
        AircraftState n2bp = Restore(ReportSeconds, "N2BP");

        Assert.Equal(AircraftSituation.VfrFlightFollowing, n2bp.Situation.Current);
        Assert.Equal(AircraftSituation.VfrArrivalInbound, SituationClassifier.Classify(n2bp, ReportSeconds, n2bp.Situation.Current));
    }

    private static AircraftState Restore(double seconds, string callsign)
    {
        using RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        Assert.NotNull(archive);
        AircraftSnapshotDto? dto = archive.ReadSnapshotAt(seconds)?.State.Aircraft.Find(a => a.Callsign == callsign);
        Assert.NotNull(dto);
        return AircraftState.FromSnapshot(dto, null);
    }
}

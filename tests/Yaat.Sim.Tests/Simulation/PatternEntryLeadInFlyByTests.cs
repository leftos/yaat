using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A pattern entry's synthetic lead-in point keeps fly-by sequencing whatever its course change.
///
/// Recording: S2-OAK-3 "VFR Sequencing" (ZOA), OAK 28R right traffic. N428KK is vectored onto
/// the entry, whose PTN-LEADIN needs a course change of about 133 degrees. The 120-degree
/// fly-over limit made the aircraft fly over that lead-in and then over PTN-ENTRY, turning
/// about 389 degrees before reaching the downwind.
/// </summary>
public class PatternEntryLeadInFlyByTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-follow-recording.zip";
    private const string Callsign = "N428KK";
    private const string LeadInName = "PTN-LEADIN";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("pattern entry lead-in flown fly-by");
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternEntryPhase", LogLevel.Debug)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    [Fact]
    public void S2Oak3_LeadEntersRightDownwind_LeadInFlownFlyBy_NoFullCircle()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        SimulationEngine? engine = BuildEngine();
        if (archive is null || engine is null)
        {
            return;
        }

        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);

            TimedSnapshot? snapshot = archive.ReadSnapshotAt(1580);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={snapshotTime}");

            var observer = new EntryTurnObserver();
            for (int dt = 1; dt <= 300; dt++)
            {
                engine.ReplayOneSecond();
                AircraftState? ac = engine.FindAircraft(Callsign);
                Assert.NotNull(ac);
                if (observer.Observe(ac, snapshotTime + dt, output))
                {
                    break;
                }
            }

            output.WriteLine($"entry at t={observer.EntryAt?.ToString() ?? "never"}, downwind at t={observer.DownwindAt?.ToString() ?? "never"}");
            output.WriteLine($"cumulative turn through the entry: {observer.CumulativeTurnDeg:F1} degrees");

            Assert.True(
                observer.LeadInSeen || observer.LeadInInstalled,
                $"{Callsign} entered the pattern with no {LeadInName} after t={snapshotTime}: the lead-in must shape the turn"
            );
            Assert.True(observer.EntrySeen, $"{Callsign} never entered PatternEntryPhase after t={snapshotTime}");
            Assert.True(observer.DownwindAt is not null, $"{Callsign} never reached DownwindPhase within 300 s of t={snapshotTime}");
            Assert.True(
                observer.CumulativeTurnDeg <= 200.0,
                $"{Callsign} turned {observer.CumulativeTurnDeg:F1} degrees between PatternEntryPhase and DownwindPhase; "
                    + "the lead-in's 133-degree change must be flown fly-by, not as a fly-over that circles"
            );
            Assert.True(observer.DownwindAt <= 1660, $"{Callsign} reached DownwindPhase at t={observer.DownwindAt}, expected by t=1660");
        }
    }

    /// <summary>
    /// The same 133-degree geometry twice: a fix marked ForceFlyBy (as the pattern entry's lead-in is) is sequenced at
    /// its fly-by anticipation distance, well short of the fix; a plain fix at the same geometry is flown fly-over and
    /// sequenced within 0.5 nm of it.
    /// </summary>
    [Fact]
    public void LeadInNeeding133Degrees_FliesFlyBy_AndAPlainFixAtTheSameGeometry_StillFliesFlyOver()
    {
        TestVnasData.EnsureInitialized();

        double flyByNm = SequencedDistanceNm("PTN-LEADIN", forceFlyBy: true);
        double flyOverNm = SequencedDistanceNm("XXFIX", forceFlyBy: false);

        output.WriteLine($"sequenced {flyByNm:F2} nm from the forced fly-by fix, {flyOverNm:F2} nm from the plain fix");
        Assert.True(flyByNm > 0.6, $"A ForceFlyBy fix needing 133 degrees should start its turn at the fly-by anticipation, got {flyByNm:F2} nm");
        Assert.True(flyOverNm <= 0.6, $"A plain fix needing 133 degrees is still flown fly-over and sequenced at 0.5 nm, got {flyOverNm:F2} nm");
    }

    /// <summary>
    /// Watches one aircraft through a pattern entry: whether its lead-in was seen in the route or installed on the phase,
    /// the heading it has turned between the entry and the downwind, and the second it reached the downwind.
    /// </summary>
    private sealed class EntryTurnObserver
    {
        private TrueHeading? _previousHeading;

        public bool LeadInSeen { get; private set; }
        public bool LeadInInstalled { get; private set; }
        public bool EntrySeen { get; private set; }
        public int? EntryAt { get; private set; }
        public int? DownwindAt { get; private set; }
        public double CumulativeTurnDeg { get; private set; }

        /// <summary>Observes the aircraft at second <paramref name="now"/>; true once it is on the downwind.</summary>
        public bool Observe(AircraftState ac, int now, ITestOutputHelper output)
        {
            if ((ac.Targets.NavigationRoute.Count > 0) && (ac.Targets.NavigationRoute[0].Name == LeadInName))
            {
                LeadInSeen = true;
            }

            if (ac.Phases?.CurrentPhase is DownwindPhase)
            {
                DownwindAt = now;
                return true;
            }

            if (ac.Phases?.CurrentPhase is not PatternEntryPhase entry)
            {
                return false;
            }

            if (!EntrySeen)
            {
                EntrySeen = true;
                EntryAt = now;
                _previousHeading = ac.TrueHeading;
                LeadInInstalled = (entry.LeadInLat is not null) && (entry.LeadInLon is not null);
                output.WriteLine(
                    $"first entry tick t={now}: lead-in installed={LeadInInstalled}, "
                        + $"route=[{string.Join(", ", ac.Targets.NavigationRoute.Select(t => t.Name))}]"
                );
                return false;
            }

            if (_previousHeading is not null)
            {
                CumulativeTurnDeg += _previousHeading.Value.AbsAngleTo(ac.TrueHeading);
            }

            _previousHeading = ac.TrueHeading;
            return false;
        }
    }

    /// <summary>
    /// Flies an SR22 from 5 nm short of a fix that a 133-degree right turn follows, and returns how far the fix was when
    /// the sequencer passed it.
    /// </summary>
    private static double SequencedDistanceNm(string name, bool forceFlyBy)
    {
        var start = new LatLon(37.0, -122.0);
        LatLon fix = GeoMath.ProjectPoint(start, new TrueHeading(0), 10.0);
        LatLon next = GeoMath.ProjectPoint(fix, new TrueHeading(133), 2.0);

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "SR22",
            Position = GeoMath.ProjectPoint(start, new TrueHeading(0), 5.0),
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 1000,
            IndicatedAirspeed = 120,
        };
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = name,
                Position = fix,
                ForceFlyBy = forceFlyBy,
            }
        );
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "NEXT", Position = next });

        int seconds = 0;
        while ((seconds < 600) && (aircraft.Targets.NavigationRoute.Count > 0) && (aircraft.Targets.NavigationRoute[0].Name == name))
        {
            FlightPhysics.Update(aircraft, 1.0);
            seconds++;
        }

        Assert.True(
            (aircraft.Targets.NavigationRoute.Count > 0) && (aircraft.Targets.NavigationRoute[0].Name != name),
            $"hung: {name} was still the first route fix after {seconds} s"
        );
        return GeoMath.DistanceNm(aircraft.Position, fix);
    }
}

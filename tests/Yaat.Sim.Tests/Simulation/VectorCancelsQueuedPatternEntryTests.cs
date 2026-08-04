using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A controller sets up a pattern arrival with <c>DCT VPCOL; ERD 28R</c> — fly direct
/// VPCOL, then enter the right downwind for 28R once the fix is reached — and then
/// changes their mind and vectors the aircraft with <c>RELR 20</c>.
///
/// The vector replaces the lateral plan, so the queued entry must be cancelled. Before
/// the fix the entry survived: a pattern-entry command classified as
/// <see cref="TrackedCommandType.Immediate"/> reported no dimensions, so the
/// supersede-split kept the block, and clearing the nav route completed the DCT block
/// and advanced the queue straight into it. The aircraft turned 20° right and then
/// immediately snapped back onto the downwind lead-in.
///
/// Vertical and speed assignments must still leave the queued entry alone — issuing
/// <c>DM 1500</c> on the way to the fix is ordinary, and the entry is still wanted.
/// </summary>
public class VectorCancelsQueuedPatternEntryTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/8201087a0088.zip";

    /// <summary>
    /// Snapshot to restore from: before the <c>DCT VPCOL; ERD 28R</c> at t=2706, so the compound is
    /// dispatched live during the replayed window. Restoring any later would restore the queue from
    /// the snapshot instead, and a restored block carries no <c>ParsedCommands</c> — the dispatcher
    /// cannot split it, drops it wholesale, and the bug cannot reproduce.
    /// </summary>
    private const int SetupSnapshotSeconds = 2700;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Headline: a relative vector issued while an ERD sits queued behind a DCT cancels
    /// the entry outright — no pattern phases build, and the aircraft keeps the vector.
    /// </summary>
    [Fact]
    public void RelativeVector_CancelsQueuedPatternEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST101");

        Assert.True(engine.SendCommand("TST101", "DCT VPCOL; ERD 28R").Success);

        var ac = engine.FindAircraft("TST101");
        Assert.NotNull(ac);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued ERD before the vector

        var vector = engine.SendCommand("TST101", "RELR 20");
        Assert.True(vector.Success, vector.Message);

        ac = engine.FindAircraft("TST101");
        Assert.NotNull(ac);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("ERD 28R", StringComparison.Ordinal));

        double assignedHeading = ac.Targets.AssignedMagneticHeading?.Degrees ?? -1;

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
        }

        ac = engine.FindAircraft("TST101");
        Assert.NotNull(ac);
        output.WriteLine($"after 60s: phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} hdg={ac.MagneticHeading.Degrees:F0}");

        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.DoesNotContain(ac.Phases?.Phases ?? [], p => p is DownwindPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(assignedHeading, ac.Targets.AssignedMagneticHeading?.Degrees ?? -1, 3);
    }

    /// <summary>
    /// An absolute vector (FH) cancels the queued entry for the same reason.
    /// </summary>
    [Fact]
    public void AbsoluteHeading_CancelsQueuedPatternEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST102");

        Assert.True(engine.SendCommand("TST102", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TST102", "FH 090").Success);

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
        }

        var ac = engine.FindAircraft("TST102");
        Assert.NotNull(ac);
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
    }

    /// <summary>
    /// Re-routing laterally with a fresh DCT also cancels the entry — the fix it was
    /// chained behind is no longer on the route.
    /// </summary>
    [Fact]
    public void FreshDirect_CancelsQueuedPatternEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST103");

        Assert.True(engine.SendCommand("TST103", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TST103", "DCT SUNOL").Success);

        var ac = engine.FindAircraft("TST103");
        Assert.NotNull(ac);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Null(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// Regression: an altitude assignment is not a lateral change, so the queued entry
    /// survives and still fires when the aircraft reaches the fix.
    /// </summary>
    [Fact]
    public void AltitudeAssignment_PreservesQueuedPatternEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST104");

        Assert.True(engine.SendCommand("TST104", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TST104", "DM 1500").Success);

        var ac = engine.FindAircraft("TST104");
        Assert.NotNull(ac);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued ERD survives DM
        Assert.Contains(ac.Targets.NavigationRoute, f => f.Name == "VPCOL");
    }

    /// <summary>
    /// Regression: a speed assignment likewise leaves the queued entry alone.
    /// </summary>
    [Fact]
    public void SpeedAssignment_PreservesQueuedPatternEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST105");

        Assert.True(engine.SendCommand("TST105", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TST105", "SPD 100").Success);

        var ac = engine.FindAircraft("TST105");
        Assert.NotNull(ac);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued ERD survives SPD
        Assert.Contains(ac.Targets.NavigationRoute, f => f.Name == "VPCOL");
    }

    /// <summary>
    /// A queued approach clearance has the identical shape — chained behind a DCT it is the
    /// lateral plan for the rest of the arrival — so a vector cancels it too.
    /// </summary>
    [Theory]
    [InlineData("CAPP I28R")]
    [InlineData("CVA 28R")]
    [InlineData("JFAC 28R")]
    public void Vector_CancelsQueuedApproachClearance(string clearance)
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST106");

        Assert.True(engine.SendCommand("TST106", $"DCT VPCOL; {clearance}").Success);

        var ac = engine.FindAircraft("TST106");
        Assert.NotNull(ac);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued clearance before the vector

        Assert.True(engine.SendCommand("TST106", "RELR 20").Success);

        ac = engine.FindAircraft("TST106");
        Assert.NotNull(ac);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
        }

        ac = engine.FindAircraft("TST106");
        Assert.NotNull(ac);
        output.WriteLine(
            $"after 60s: phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} approach={ac.Phases?.ActiveApproach?.RunwayId ?? "(none)"}"
        );
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.Null(ac.Approach.PendingClearance);
    }

    /// <summary>
    /// Regression: an altitude assignment leaves a queued approach clearance alone.
    /// </summary>
    [Fact]
    public void AltitudeAssignment_PreservesQueuedApproachClearance()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST107");

        Assert.True(engine.SendCommand("TST107", "DCT VPCOL; CAPP I28R").Success);
        Assert.True(engine.SendCommand("TST107", "DM 1500").Success);

        var ac = engine.FindAircraft("TST107");
        Assert.NotNull(ac);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Contains(ac.Targets.NavigationRoute, f => f.Name == "VPCOL");
    }

    /// <summary>
    /// Regression for the pairing that makes the queued-clearance rule safe: CFIX annotates the
    /// route instead of replacing it, so a chain of crossing restrictions must leave both the
    /// queued approach clearance and the DCT the aircraft is flying alone. This is the
    /// hand-built descend-via profile from <see cref="MultiCfixPresetReplayTests"/> in miniature.
    /// </summary>
    [Fact]
    public void CrossingRestrictions_PreserveQueuedApproachClearanceAndRoute()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST108");

        Assert.True(engine.SendCommand("TST108", "DCT VPCOL; CAPP I28R").Success);
        Assert.True(engine.SendCommand("TST108", "CFIX VPCOL 15 180").Success);

        var ac = engine.FindAircraft("TST108");
        Assert.NotNull(ac);
        Assert.Empty(ac.PendingWarnings);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued CAPP survives the crossing restriction

        var vpcol = ac.Targets.NavigationRoute.Find(f => f.Name == "VPCOL");
        Assert.NotNull(vpcol);
        Assert.NotNull(vpcol.AltitudeRestriction);
    }

    /// <summary>
    /// The reported session, replayed. N805FM is handed <c>DCT VPCOL; ERD 28R</c> at t=2706 and
    /// vectored with <c>RELR 20</c> at t=2735; the recording shows the pattern chain
    /// (PatternEntry → Downwind → Base → FinalApproach → Landing) installing itself ten seconds
    /// later and 28R being assigned, with the aircraft turning straight back toward the downwind.
    ///
    /// Hybrid replay: the fix changes what a lateral command does to the queue, and this session
    /// runs 45 minutes of pattern work at OAK before the moment of interest, so replaying it all
    /// from t=0 with the new dispatch rules would not reproduce the same setup. The snapshot pins
    /// the pre-vector state the controller actually saw.
    /// </summary>
    [Fact]
    public void Recording_RelrAfterQueuedErd_DoesNotEnterTheDownwind()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snapshot = archive.ReadSnapshotAt(SetupSnapshotSeconds);
        if (snapshot is null)
        {
            return;
        }

        engine.RestoreFromSnapshot(snapshot.State);

        int start = (int)snapshot.ElapsedSeconds;
        bool sawQueuedEntry = false;
        bool vectored = false;

        // DCT VPCOL; ERD 28R lands at t=2706 and RELR 20 at t=2735; run past where the recording
        // shows the entry firing anyway (t=2745).
        for (int t = start + 1; t <= 2760; t++)
        {
            engine.ReplayRange(t - 1, t, recording.Actions);
            var ac = engine.FindAircraft("N805FM");
            Assert.NotNull(ac);

            var route = string.Join(",", ac.Targets.NavigationRoute.Select(f => f.Name));
            output.WriteLine(
                $"t={t} phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} hdg={ac.MagneticHeading.Degrees:F0} route=[{route}]"
            );

            if (!vectored)
            {
                sawQueuedEntry |= ac.Queue.Blocks.Exists(b => !b.IsApplied);
                vectored = ac.Targets.AssignedMagneticHeading is not null;
                continue;
            }

            Assert.Null(ac.Phases?.CurrentPhase);
            Assert.DoesNotContain(ac.Targets.NavigationRoute, f => f.Name.StartsWith("PTN-", StringComparison.Ordinal));
        }

        Assert.True(sawQueuedEntry, "the ERD 28R never reached the queue — the replay did not set the bug up");
        Assert.True(vectored, "RELR 20 was never applied during the replayed window");
    }

    private static void SpawnAirborneOverOak(SimulationEngine engine, string callsign)
    {
        // A few miles east of OAK 28R on the right downwind side, slow VFR piston.
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "DA62",
            Position = new LatLon(37.66, -122.16),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 2000,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 150,
            },
        };
        engine.World.AddAircraft(ac);
    }
}

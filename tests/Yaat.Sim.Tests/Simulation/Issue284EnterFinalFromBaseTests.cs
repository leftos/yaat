using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E + synthetic tests for GitHub issue #284: issuing <c>EF</c> to an aircraft on a base
/// leg routed it outbound down the final.
///
/// Recording: S2-OAK-P "S2 Rating Practical Exam". N346G (C150, VFR closed traffic, right
/// traffic 28R) is on a right base at t≈1799 — 0.90 nm along-track from the 28L threshold,
/// 0.36 nm right of its centerline, heading 199° (93° off the 292° final course), 500 ft AGL.
/// The controller issues <c>EF 28L</c>.
///
/// Before the fix, every guard in TryEnterPattern missed: the parallel sidestep and the
/// same-runway no-op both require FinalApproachPhase; the close-in path requires alignment;
/// and ComputeAltitudeAwareFinalEntryDistanceNm — whose whole job is "capped at along-track
/// so EF never routes the aircraft outbound" — returns null because 0.90 nm is inside the
/// 1.0 nm piston straight-in floor. The fixed fallback then planted PTN-ENTRY 3.14 nm out on
/// the 28L final, 2.24 nm BEHIND the aircraft, and it flew away from the field.
///
/// After the fix an inbound/crossing aircraft inside the straight-in floor, whose target
/// centerline is still ahead of it, continues its base and turns final onto that centerline
/// (7110.65 §3-10-5.c "change to runway"; AIM FIG 4-3-2 note 3 allows the turn to final to
/// complete 1/4 mile out). Aircraft that cannot fly it — jets/turboprops, or anything that has
/// already overshot the centerline — reject rather than looping outbound.
///
/// Every case runs against the real OAK runways from NavData (TestVnasData), not a synthetic
/// stub — the closely-spaced 28L/28R geometry (~1000 ft apart) is the whole point.
/// </summary>
public class Issue284EnterFinalFromBaseTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue284-ef-base-outbound-recording.zip";

    /// <summary>Sim time (s) of the last snapshot before the user typed <c>EF 28L</c> at t=1799.</summary>
    private const int SnapshotSeconds = 1795;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("PatternCommandHandler", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    // -------------------------------------------------------------------------
    // Case (a) — E2E from the recording
    // -------------------------------------------------------------------------

    /// <summary>
    /// N346G on a right base for 28R, given <c>EF 28L</c>, must continue its base and turn
    /// final onto the 28L centerline — never navigate to an entry point behind it.
    /// </summary>
    [Fact]
    public void N346G_EfToParallelFromBase_TurnsFinalInsteadOfFlyingOutbound()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(archive.ToBaseSessionRecording(), 0);

            var snapshot = archive.ReadSnapshotAt(SnapshotSeconds);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={SnapshotSeconds} — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var aircraft = engine.FindAircraft("N346G");
            Assert.NotNull(aircraft);

            var rwy28L = NavigationDatabase.Instance.GetRunway("KOAK", "28L");
            Assert.NotNull(rwy28L);

            double alongBefore = AlongTrackToThresholdNm(aircraft, rwy28L);
            output.WriteLine(
                $"N346G: phase={aircraft.Phases?.CurrentPhase?.Name} hdg={aircraft.TrueHeading.Degrees:F0} "
                    + $"alt={aircraft.Altitude:F0} alongTrack(28L)={alongBefore:F2}nm assignedRwy={aircraft.Phases?.AssignedRunway?.Designator}"
            );

            // Mirror what the user typed. EF carries no L/R, so the dispatcher passes null.
            var result = PatternCommandHandler.TryEnterPattern(aircraft, requestedDirection: null, PatternEntryLeg.Final, "28L", null);
            output.WriteLine($"TryEnterPattern(28L) -> Success={result.Success} Message='{result.Message}'");

            Assert.True(result.Success, $"EF 28L from a 0.9 nm base must be accepted. Got: '{result.Message}'");

            // No PatternEntryPhase: the aircraft turns final from where it is, it does not
            // navigate to a waypoint 3.14 nm out on the final.
            Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is PatternEntryPhase);

            var phases = aircraft.Phases.Phases;
            Assert.Collection(
                phases,
                p => Assert.IsType<BasePhase>(p),
                p => Assert.IsType<FinalApproachPhase>(p),
                p => Assert.IsType<TouchAndGoPhase>(p)
            );

            Assert.Equal("28L", aircraft.Phases.AssignedRunway?.Designator);

            // The aircraft is north of the 28L centerline — that is a RIGHT base for 28L,
            // regardless of 28L's default (left) pattern side.
            Assert.Equal(PatternDirection.Right, aircraft.Phases.TrafficDirection);
        }
    }

    /// <summary>
    /// The direct statement of the bug: after <c>EF 28L</c> the aircraft's distance to the
    /// 28L threshold must never increase. In the recording it grew from 0.90 nm outward while
    /// the aircraft climbed back toward pattern altitude.
    /// </summary>
    [Fact]
    public void N346G_AfterEf_NeverFliesAwayFromTheThreshold()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(archive.ToBaseSessionRecording(), 0);
            var snapshot = archive.ReadSnapshotAt(SnapshotSeconds);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var aircraft = engine.FindAircraft("N346G");
            Assert.NotNull(aircraft);
            var rwy28L = NavigationDatabase.Instance.GetRunway("KOAK", "28L");
            Assert.NotNull(rwy28L);

            var result = PatternCommandHandler.TryEnterPattern(aircraft, requestedDirection: null, PatternEntryLeg.Final, "28L", null);
            Assert.True(result.Success, result.Message);

            double startAltitude = aircraft.Altitude;
            double worstDistNm = DistanceToThresholdNm(aircraft, rwy28L);
            double worstAltitude = startAltitude;

            for (int t = 1; t <= 60; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft("N346G");
                if (ac is null)
                {
                    break;
                }

                double dist = DistanceToThresholdNm(ac, rwy28L);
                worstDistNm = Math.Max(worstDistNm, dist);
                worstAltitude = Math.Max(worstAltitude, ac.Altitude);

                if (t % 10 == 0)
                {
                    output.WriteLine(
                        $"t=+{t}s phase={ac.Phases?.CurrentPhase?.Name} hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0} dist28L={dist:F2}nm"
                    );
                }
            }

            double endDistNm = DistanceToThresholdNm(engine.FindAircraft("N346G")!, rwy28L);
            output.WriteLine($"worstDist={worstDistNm:F2}nm worstAlt={worstAltitude:F0} endDist={endDistNm:F2}nm");

            // On the retarget the aircraft flies the rest of its base (cross-track shrinks, so the
            // straight-line distance to the threshold can only fall) then turns final. It must never
            // wander outbound the way it did in the recording (0.90 -> 1.46 nm and still climbing).
            Assert.True(worstDistNm < 1.15, $"EF must not route the aircraft away from the field; peak distance to 28L was {worstDistNm:F2} nm");
            Assert.True(endDistNm < 0.97, $"Aircraft should be closing on the 28L threshold; ended {endDistNm:F2} nm out");

            // It also must not climb back to pattern altitude (recording: 509 -> 671 ft and rising).
            Assert.True(
                worstAltitude < startAltitude + 50,
                $"EF must not climb the aircraft; peaked at {worstAltitude:F0} ft from {startAltitude:F0} ft"
            );
        }
    }

    // -------------------------------------------------------------------------
    // Synthetic cases — OAK 28R only, so the bug is stated runway-agnostically
    // -------------------------------------------------------------------------

    /// <summary>
    /// Same code hole, same runway: a piston on a base leg 0.9 nm along-track for the runway
    /// it is already flying gets <c>EF</c> for that same runway. It must turn final from its
    /// present position, not fly out to the fixed 3.14 nm entry point.
    /// </summary>
    [Fact]
    public void Piston_EfSameRunwayFromBase_TurnsFinalFromPresentPosition()
    {
        var runway = RealOakRunway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeBaseLegAircraft(runway, "C150", alongTrackNm: 0.9, crossTrackRightNm: 0.4, altAgl: 500);

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Final, "28R", null);
        output.WriteLine($"TryEnterPattern(28R) -> Success={result.Success} Message='{result.Message}'");

        Assert.True(result.Success, $"Piston on a 0.9 nm base must turn final. Got: '{result.Message}'");
        Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is PatternEntryPhase);
        Assert.IsType<BasePhase>(aircraft.Phases.Phases[0]);
        Assert.Equal(PatternDirection.Right, aircraft.Phases.TrafficDirection);
    }

    /// <summary>
    /// The <c>trackingOutbound</c> escape. An aircraft on downwind abeam the threshold has a
    /// small positive along-track and the standard entry point behind it, exactly like the base
    /// leg — but it is already tracking outbound (AIM 4-3-2.c.4), so flying to that entry point
    /// and turning onto final is the normal pattern, not a reversal. It must still be accepted
    /// with the far entry.
    /// </summary>
    [Fact]
    public void Piston_EfFromDownwindAbeam_StillUsesStandardFarEntry()
    {
        var runway = RealOakRunway("28R");
        if (runway is null)
        {
            return;
        }

        // Abeam the threshold, 0.7 nm right of centerline, tracking the runway reciprocal.
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 0.3, crossTrackRightNm: 0.7);
        var aircraft = MakeAircraft("N1DW", "C150", lat, lon, runway.ElevationFt + 1000, runway.TrueHeading.ToReciprocal().Degrees);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Final, "28R", null);
        output.WriteLine($"TryEnterPattern(28R) -> Success={result.Success} Message='{result.Message}'");

        Assert.True(result.Success, $"EF from downwind abeam must still be accepted. Got: '{result.Message}'");
        var entry = Assert.Single(aircraft.Phases!.Phases.OfType<PatternEntryPhase>());
        double entryDist = GeoMath.DistanceNm(
            new LatLon(entry.EntryLat, entry.EntryLon),
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude)
        );
        output.WriteLine($"entry {entryDist:F2} nm from threshold");
        Assert.InRange(entryDist, 2.5, 3.5);
    }

    /// <summary>
    /// A jet cannot fly a stabilized pattern final that short, so the retarget window is empty
    /// for it and the never-outbound clamp rejects rather than routing it outbound.
    /// </summary>
    [Fact]
    public void Jet_EfFromBaseInsideStraightInFloor_Rejects()
    {
        var runway = RealOakRunway("28R");
        if (runway is null)
        {
            return;
        }

        // 1.5 nm along-track is inside the jet 2.0 nm straight-in floor.
        var aircraft = MakeBaseLegAircraft(runway, "B738", alongTrackNm: 1.5, crossTrackRightNm: 0.8, altAgl: 700);

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Final, "28R", null);
        output.WriteLine($"TryEnterPattern(28R) -> Success={result.Success} Message='{result.Message}'");

        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Overshoot guardrail: an aircraft that has already crossed the target centerline cannot
    /// fly a normal base-to-final onto it — reaching it would need an S-turn back across a final
    /// it has already passed. Reject; the controller issues a go-around.
    /// </summary>
    [Fact]
    public void Piston_EfFromBaseAfterOvershootingCenterline_Rejects()
    {
        var runway = RealOakRunway("28R");
        if (runway is null)
        {
            return;
        }

        // Same base heading (turning right-to-left across the centerline) but already 0.4 nm
        // through it, on the LEFT side — the centerline is behind, not ahead.
        var aircraft = MakeBaseLegAircraft(runway, "C150", alongTrackNm: 0.9, crossTrackRightNm: -0.4, altAgl: 500);

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Final, "28R", null);
        output.WriteLine($"TryEnterPattern(28R) -> Success={result.Success} Message='{result.Message}'");

        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Continuity at the straight-in floor: just OUTSIDE it (1.05 nm > the 1.0 nm piston floor)
    /// the altitude-aware diagonal join handles the geometry and places the entry ahead of the
    /// aircraft, never behind it.
    /// </summary>
    [Fact]
    public void Piston_EfFromBaseJustOutsideFloor_UsesDiagonalJoinAheadOfAircraft()
    {
        var runway = RealOakRunway("28R");
        if (runway is null)
        {
            return;
        }

        // 1.5 nm of cross-track keeps the join point more than 1 nm away, so a PatternEntryPhase is
        // actually inserted (inside 1 nm the aircraft simply joins final with no entry waypoint).
        var aircraft = MakeBaseLegAircraft(runway, "C150", alongTrackNm: 1.05, crossTrackRightNm: 1.5, altAgl: 500);

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Final, "28R", null);
        output.WriteLine($"TryEnterPattern(28R) -> Success={result.Success} Message='{result.Message}'");
        Assert.True(result.Success, result.Message);

        // Just outside the floor the diagonal join owns the geometry: the entry point is capped at the
        // aircraft's along-track (never outbound of it) and respects the piston 1.0 nm minimum final.
        var entry = Assert.Single(aircraft.Phases!.Phases.OfType<PatternEntryPhase>());
        double entryDist = GeoMath.DistanceNm(
            new LatLon(entry.EntryLat, entry.EntryLon),
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude)
        );
        output.WriteLine($"entry {entryDist:F2} nm from threshold (aircraft along-track 1.05 nm)");
        Assert.InRange(entryDist, 1.0 - 1e-6, 1.05 + 1e-6);
    }

    /// <summary>
    /// A rejected <c>EF</c> makes the pilot transmit "unable" in solo-training mode. This already
    /// works via <c>CommandDefinition.ProducesPilotUnable</c> (true for the whole Pattern category)
    /// plus the <c>RejectedCommandType</c> stamped by <c>CommandDispatcher.WithRejectedCommand</c>;
    /// the test pins it so the reject branch of the never-outbound clamp stays audible to a student.
    /// RPO mode is silent for both rejects and readbacks by design.
    /// </summary>
    [Fact]
    public void RejectedEf_InSoloTraining_QueuesPilotUnableTransmission()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(archive.ToBaseSessionRecording(), 0);
            var snapshot = archive.ReadSnapshotAt(SnapshotSeconds);
            if (snapshot is null || engine.Scenario is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            engine.Scenario.SoloTrainingMode = true;

            var rwy28R = NavigationDatabase.Instance.GetRunway("KOAK", "28R");
            Assert.NotNull(rwy28R);

            var aircraft = engine.FindAircraft("N346G");
            Assert.NotNull(aircraft);

            // Nudge N346G past the 28R centerline so the runway it is asked to join is behind
            // it — the overshoot guardrail rejects rather than looping it outbound. Set the
            // position directly: WarpAircraft is a ground warp and would clear the phase chain.
            var (lat, lon) = PositionFromThreshold(rwy28R, alongTrackOutboundNm: 0.9, crossTrackRightNm: -0.4);
            aircraft.Position = new LatLon(lat, lon);
            aircraft.TrueHeading = new TrueHeading((rwy28R.TrueHeading.Degrees - 90 + 360) % 360);
            aircraft.PendingPilotTransmissions.Clear();

            var result = engine.SendCommand("N346G", "EF 28R");
            output.WriteLine($"SendCommand(EF 28R) -> Success={result.Success} Message='{result.Message}'");
            foreach (var tx in aircraft.PendingPilotTransmissions)
            {
                output.WriteLine($"  pilot: terminal='{tx.Text}' speech='{tx.SpeechText}'");
            }

            Assert.False(result.Success);
            Assert.Contains(aircraft.PendingPilotTransmissions, tx => tx.Text.Contains("unable", StringComparison.OrdinalIgnoreCase));
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// The real OAK runway from NavData. Returns null (test silently skips) when the data files
    /// are absent. Synthetic stubs are not used here: 28L and 28R are ~1000 ft apart and the
    /// pattern geometry depends on both being present at the airport.
    /// </summary>
    private static RunwayInfo? RealOakRunway(string designator)
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb?.GetRunway("KOAK", designator);
    }

    private static AircraftState MakeAircraft(string callsign, string type, double lat, double lon, double alt, double headingDeg) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = new LatLon(lat, lon),
            Altitude = alt,
            TrueHeading = new TrueHeading(headingDeg),
            IndicatedAirspeed = type == "B738" ? 150 : 65,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Phases = new PhaseList(),
        };

    /// <summary>
    /// Places an aircraft on a base leg for <paramref name="runway"/>: perpendicular to the
    /// final course, flying from the right (north) side toward the centerline. A negative
    /// <paramref name="crossTrackRightNm"/> puts it past the centerline (overshot).
    /// </summary>
    private static AircraftState MakeBaseLegAircraft(RunwayInfo runway, string type, double alongTrackNm, double crossTrackRightNm, double altAgl)
    {
        var (lat, lon) = PositionFromThreshold(runway, alongTrackNm, crossTrackRightNm);
        double baseHeading = (runway.TrueHeading.Degrees - 90 + 360) % 360;
        var aircraft = MakeAircraft("N346G", type, lat, lon, runway.ElevationFt + altAgl, baseHeading);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Phases.Add(new BasePhase());
        return aircraft;
    }

    private static (double Lat, double Lon) PositionFromThreshold(RunwayInfo runway, double alongTrackOutboundNm, double crossTrackRightNm)
    {
        var centerline = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading.ToReciprocal(),
            alongTrackOutboundNm
        );
        double crossHdg = crossTrackRightNm >= 0 ? (runway.TrueHeading.Degrees + 90) % 360 : (runway.TrueHeading.Degrees + 270) % 360;
        var result = GeoMath.ProjectPoint(centerline.Lat, centerline.Lon, new TrueHeading(crossHdg), Math.Abs(crossTrackRightNm));
        return (result.Lat, result.Lon);
    }

    private static double AlongTrackToThresholdNm(AircraftState aircraft, RunwayInfo runway) =>
        GeoMath.AlongTrackDistanceNm(
            aircraft.Position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            runway.TrueHeading.ToReciprocal()
        );

    private static double DistanceToThresholdNm(AircraftState aircraft, RunwayInfo runway) =>
        GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
}

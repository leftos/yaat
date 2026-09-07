using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An aircraft already established on a pattern leg that is switched to a close parallel runway
/// transitions leg to leg instead of crossing the field: it continues the upwind it is flying and
/// turns crosswind only beyond BOTH runways' departure ends (AIM 4-3-2 — the crosswind turn is
/// commenced beyond the departure end of the runway), then flies the new runway's crosswind onto
/// its downwind. OAK 28L/28R centerlines are 0.165 nm apart, so there is no field to cross.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA, OAK). N342T (DA42) goes around off
/// 28R at t≈1110 and climbs the 28R upwind in right traffic. Both tests restore the go-around
/// snapshot, tick the engine forward with <see cref="SimulationEngine.TickOneSecond"/> and issue the
/// pattern change directly — the recorded MLT is never replayed.
/// </summary>
public class ParallelRunwayMltFromUpwindTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/parallel-mlt-from-upwind-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N342T";
    private const string AirportId = "OAK";

    // The aircraft is in GoAroundPhase here; ticking forward completes the climb-out and the
    // auto-cycle appends the 28R right-traffic circuit it was flying before the go-around.
    private const int GoAroundSnapshotTime = 1120;

    private const int MaxTicksToUpwind = 40;
    private const int MaxTicksAfterCommand = 200;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .EnableCategory("CrosswindPhase", LogLevel.Debug)
            .EnableCategory("MidfieldCrossingPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void Mlt28L_OnUpwind28R_ContinuesUpwindAndTurnsBeyondBothDepartureEnds()
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

            var aircraft = RestoreAndClimbToUpwind(engine, archive, "28R", PatternDirection.Right);
            if (aircraft is null)
            {
                return;
            }

            var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R")!;
            var rwy28L = NavigationDatabase.Instance.GetRunway(AirportId, "28L")!;

            var result = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, "28L", null, aircraft.Ground.Layout);
            Assert.True(result.Success, $"MLT 28L was refused: {result.Message}");

            AssertTransitionInstalled(aircraft, rwy28L, rwy28R);
            FlyTheTransition(engine, rwy28L, rwy28R, PatternDirection.Left);
        }
    }

    [Fact]
    public void MrtBackTo28R_AfterMlt28L_OnUpwind_TurnsBeyondBothDepartureEnds()
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

            var aircraft = RestoreAndClimbToUpwind(engine, archive, "28R", PatternDirection.Right);
            if (aircraft is null)
            {
                return;
            }

            var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R")!;
            var rwy28L = NavigationDatabase.Instance.GetRunway(AirportId, "28L")!;

            // First switch: the aircraft is now on a 28L transition upwind (its waypoints carry 28L's
            // threshold), so the second command is itself a runway switch back to 28R.
            var toLeft = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, "28L", null, aircraft.Ground.Layout);
            Assert.True(toLeft.Success, $"MLT 28L was refused: {toLeft.Message}");
            engine.TickOneSecond();

            aircraft = engine.FindAircraft(Callsign)!;
            var back = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, "28R", null, aircraft.Ground.Layout);
            Assert.True(back.Success, $"MRT 28R was refused: {back.Message}");

            AssertTransitionInstalled(aircraft, rwy28R, rwy28L);
            FlyTheTransition(engine, rwy28R, rwy28L, PatternDirection.Right);
        }
    }

    /// <summary>
    /// Restore the go-around snapshot and tick until the aircraft is climbing out on the expected
    /// runway's upwind. Returns null when the fixture is unavailable (skip-return, like the model test).
    /// </summary>
    private AircraftState? RestoreAndClimbToUpwind(SimulationEngine engine, RecordingArchive archive, string runwayId, PatternDirection direction)
    {
        engine.Replay(archive.ToBaseSessionRecording(), 0);

        var snapshot = archive.ReadSnapshotAt(GoAroundSnapshotTime);
        if (snapshot is null)
        {
            output.WriteLine($"No snapshot near t={GoAroundSnapshotTime} — skipping");
            return null;
        }
        engine.RestoreFromSnapshot(snapshot.State);

        for (int i = 0; i < MaxTicksToUpwind; i++)
        {
            var ac = engine.FindAircraft(Callsign);
            if (
                (ac?.Phases?.CurrentPhase is UpwindPhase)
                && string.Equals(ac.Phases.AssignedRunway?.Designator, runwayId, StringComparison.OrdinalIgnoreCase)
                && (ac.Phases.TrafficDirection == direction)
            )
            {
                output.WriteLine(
                    $"Upwind {runwayId}/{direction} reached after {i} ticks: pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5}) "
                        + $"hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0}"
                );
                return ac;
            }
            engine.TickOneSecond();
        }

        Assert.Fail($"{Callsign} never reached the {runwayId}/{direction} upwind within {MaxTicksToUpwind} ticks of t={GoAroundSnapshotTime}");
        return null;
    }

    /// <summary>
    /// The chain is a leg-to-leg transition: the new runway's pattern, no midfield crossing, and an
    /// upwind whose crosswind turn point lies beyond both runways' departure ends.
    /// </summary>
    private void AssertTransitionInstalled(AircraftState aircraft, RunwayInfo patternRunway, RunwayInfo otherRunway)
    {
        var chain = aircraft.Phases?.Phases ?? [];
        output.WriteLine($"chain=[{string.Join(",", chain.Select(p => $"{p.GetType().Name}:{p.Status}"))}]");

        Assert.Equal(patternRunway.Designator, aircraft.Phases?.AssignedRunway?.Designator);
        Assert.DoesNotContain(chain, p => p is MidfieldCrossingPhase);

        var upwind = Assert.IsType<UpwindPhase>(aircraft.Phases?.CurrentPhase);
        var waypoints = upwind.Waypoints;
        Assert.NotNull(waypoints);

        double turnAlongTrack = AlongTrack(patternRunway, waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon);
        double patternDer = AlongTrack(patternRunway, patternRunway.EndLatitude, patternRunway.EndLongitude);
        double otherDer = AlongTrack(patternRunway, otherRunway.EndLatitude, otherRunway.EndLongitude);
        output.WriteLine(
            $"crosswind turn along-track={turnAlongTrack:F3} nm, {patternRunway.Designator} DER={patternDer:F3}, {otherRunway.Designator} DER={otherDer:F3}"
        );

        Assert.True(
            turnAlongTrack >= patternDer - 0.001,
            $"crosswind turn point is {turnAlongTrack:F3} nm along {patternRunway.Designator}, short of its own departure end at {patternDer:F3} nm"
        );
        Assert.True(
            turnAlongTrack >= otherDer - 0.001,
            $"crosswind turn point is {turnAlongTrack:F3} nm along {patternRunway.Designator}, short of {otherRunway.Designator}'s departure end at {otherDer:F3} nm"
        );
    }

    /// <summary>
    /// Fly the transition: the upwind holds runway heading on its own side of the pattern runway
    /// until beyond both departure ends, the crosswind turns the pattern way, and the aircraft ends
    /// up established on the new runway's downwind on the pattern side.
    /// </summary>
    private void FlyTheTransition(SimulationEngine engine, RunwayInfo patternRunway, RunwayInfo otherRunway, PatternDirection direction)
    {
        double fartherDer = Math.Max(
            AlongTrack(patternRunway, patternRunway.EndLatitude, patternRunway.EndLongitude),
            AlongTrack(patternRunway, otherRunway.EndLatitude, otherRunway.EndLongitude)
        );
        // Left traffic lies left of the runway heading (negative cross-track), right traffic right of it.
        double patternSideSign = direction == PatternDirection.Left ? -1.0 : 1.0;

        double? crosswindStartAlongTrack = null;
        double maxCrosswindBank = 0;
        bool establishedOnDownwind = false;
        AircraftState? onDownwind = null;

        for (int i = 0; i < MaxTicksAfterCommand; i++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.DoesNotContain(ac.Phases?.Phases ?? [], p => p is MidfieldCrossingPhase);

            switch (ac.Phases?.CurrentPhase)
            {
                case UpwindPhase:
                    Assert.True(
                        ac.TrueHeading.AbsAngleTo(patternRunway.TrueHeading) < 15,
                        $"upwind heading {ac.TrueHeading.Degrees:F0}° is {ac.TrueHeading.AbsAngleTo(patternRunway.TrueHeading):F0}° off "
                            + $"runway {patternRunway.Designator} ({patternRunway.TrueHeading.Degrees:F0}°) — it turned before the departure ends"
                    );
                    Assert.True(
                        (CrossTrack(patternRunway, ac) * patternSideSign) <= 0.05,
                        $"upwind drifted {CrossTrack(patternRunway, ac):F3} nm onto the {direction} pattern side of {patternRunway.Designator} "
                            + "before the crosswind turn — it cut across the runway"
                    );
                    break;

                case CrosswindPhase:
                    crosswindStartAlongTrack ??= AlongTrack(patternRunway, ac.Position.Lat, ac.Position.Lon);
                    maxCrosswindBank = Math.Max(maxCrosswindBank, ac.BankAngle * patternSideSign);
                    break;

                case DownwindPhase:
                    onDownwind = ac;
                    // Established: tracking the runway's reciprocal on the pattern side. The leg
                    // re-intercepts its track after the transition crosswind, so this takes a few
                    // tens of seconds — but it must happen before the base turn ends the leg.
                    establishedOnDownwind =
                        (ac.TrueHeading.AbsAngleTo(patternRunway.TrueHeading.ToReciprocal()) < 20)
                        && ((CrossTrack(patternRunway, ac) * patternSideSign) > 0.2);
                    break;
            }

            if (establishedOnDownwind)
            {
                break;
            }
        }

        Assert.NotNull(crosswindStartAlongTrack);
        output.WriteLine(
            $"crosswind began at along-track {crosswindStartAlongTrack:F3} nm (farther DER {fartherDer:F3} nm), max pattern-side bank {maxCrosswindBank:F0}°"
        );
        Assert.True(
            crosswindStartAlongTrack >= fartherDer - 0.1,
            $"crosswind turn fired at along-track {crosswindStartAlongTrack:F3} nm, short of the farther departure end at {fartherDer:F3} nm"
        );
        Assert.True(maxCrosswindBank > 5.0, $"crosswind turn was not toward the {direction} pattern side (max bank {maxCrosswindBank:F0}°)");

        Assert.NotNull(onDownwind);
        double downwindCrossTrack = CrossTrack(patternRunway, onDownwind);
        output.WriteLine(
            $"downwind: cross-track {downwindCrossTrack:F3} nm, hdg {onDownwind.TrueHeading.Degrees:F0}° "
                + $"(reciprocal {patternRunway.TrueHeading.ToReciprocal().Degrees:F0}°), established={establishedOnDownwind}"
        );
        Assert.True(
            establishedOnDownwind,
            $"never established on {patternRunway.Designator}'s {direction} downwind — last sample was {downwindCrossTrack:F3} nm "
                + $"from the runway on heading {onDownwind.TrueHeading.Degrees:F0}° (reciprocal {patternRunway.TrueHeading.ToReciprocal().Degrees:F0}°)"
        );
    }

    private static double AlongTrack(RunwayInfo runway, double lat, double lon) =>
        GeoMath.AlongTrackDistanceNm(new LatLon(lat, lon), new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);

    private static double CrossTrack(RunwayInfo runway, AircraftState aircraft) =>
        GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);
}

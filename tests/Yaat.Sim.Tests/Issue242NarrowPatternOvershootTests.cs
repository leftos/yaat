using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #242: after a cross-runway <c>ELD</c> join (via <see cref="MidfieldCrossingPhase"/>) the
/// aircraft can be left flying the downwind INSIDE its computed pattern — at OAK 28L the pattern is a
/// full 0.5 NM (runway 30 is ~1 NM away, so nothing clamps it), yet the recorded BE36 flew the downwind
/// at ~0.28 NM. Holding that too-close offset makes the base→final geometry (built for 0.5 NM) fire the
/// turn immediately and sweep ~180° through the centerline onto the parallel (28R). The downwind leg must
/// re-intercept the computed track so it rolls out on centerline.
/// </summary>
[Collection("NavDbMutator")]
public class Issue242NarrowPatternOvershootTests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public Issue242NarrowPatternOvershootTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(Rwy()));
    }

    public void Dispose() => _navDbScope.Dispose();

    // Field elevation 9 ft mirrors OAK; heading 280 gives a left downwind to the south.
    private static RunwayInfo Rwy() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 9);

    private static PhaseContext Ctx(AircraftState ac, double dt = 1.0)
    {
        var rwy = ac.Phases!.AssignedRunway!;
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }

    [Fact]
    public void DownwindDroppedInsidePattern_ReintercptsAndRollsOutOnCenterline()
    {
        var rwy = Rwy();
        const double sizeNm = 0.5; // OAK 28L's real (unclamped) pattern width

        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Piston, PatternDirection.Left, sizeNm, null, null);

        // Simulate the cross-runway join outcome: the aircraft is on the downwind heading but INSIDE the
        // computed 0.5 NM downwind line (≈0.28 NM from the centerline), as N104NT was. The abeam point is
        // exactly 0.5 NM off the threshold on the downwind side, so lerping 0.56 of the way from the
        // threshold lands the aircraft 0.28 NM out on the correct side (abeam along-track).
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        var abeamInside = LatLon.Lerp(threshold, abeam, 0.56);
        // Back up ~0.7 NM along the downwind (upwind of abeam) so the aircraft joins at the downwind
        // start with the full leg ahead to re-intercept the track, as it would after a real join.
        var insidePos = GeoMath.ProjectPoint(abeamInside, wp.DownwindHeading.ToReciprocal(), 0.7);

        var ac = new AircraftState
        {
            Callsign = "N104NT",
            AircraftType = "BE36",
            Position = insidePos,
            TrueHeading = wp.DownwindHeading,
            TrueTrack = wp.DownwindHeading,
            Altitude = wp.PatternAltitude,
            IndicatedAirspeed = 95,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left };

        var phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Piston,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: sizeNm,
            altitudeOverrideFt: null,
            airportRunways: null
        );
        // This aircraft was dropped inside its pattern by a cross-runway join, so its downwind is a
        // rejoin (as the wrong-side / cross-runway build paths mark it).
        ((DownwindPhase)phases[0]).RejoinTrack = true;
        foreach (var p in phases)
        {
            ac.Phases.Add(p);
        }
        ac.Phases.Start(Ctx(ac));
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;

        // Left pattern → downwind is on the negative-cross-track side; a positive value is the far
        // (parallel-runway) side the aircraft must never cross to.
        double maxFarSideNm = 0.0;
        double downwindReestablishNm = 0.0; // how close to the 0.5 line the downwind got (abs cross-track)

        for (int i = 0; i < 400; i++)
        {
            var cur = ac.Phases.CurrentPhase;
            if (cur is null || ac.Phases.IsComplete)
            {
                break;
            }

            bool onDownwind = cur is DownwindPhase;

            var ctx = Ctx(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);

            double signed = GeoMath.SignedCrossTrackDistanceNm(
                ac.Position,
                new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
                rwy.TrueHeading
            );
            if (signed > maxFarSideNm)
            {
                maxFarSideNm = signed;
            }
            if (onDownwind)
            {
                downwindReestablishNm = Math.Abs(signed);
            }

            bool established =
                (ac.Phases.CurrentPhase is FinalApproachPhase or LandingPhase)
                && (Math.Abs(signed) < 0.05)
                && (ac.TrueHeading.AbsAngleTo(rwy.TrueHeading) < 5.0);
            if (established)
            {
                break;
            }
        }

        // The downwind should have re-intercepted the computed 0.5 NM track (got back out near 0.5),
        // not held the ~0.28 NM drop-in offset.
        Assert.True(
            downwindReestablishNm >= 0.42,
            $"Downwind never re-intercepted the 0.5 NM track (closest approach to centerline stayed at {downwindReestablishNm:F3} NM)."
        );

        // And it must roll out on centerline without sweeping across to the parallel side.
        Assert.True(maxFarSideNm <= 0.05, $"Overshot the runway centerline by {maxFarSideNm:F3} NM onto the far (parallel) side (issue #242).");
    }
}

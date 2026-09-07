using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// An option clearance's pattern modifier (<c>COPT MLT 28L</c> issued on the 28R circuit) arms
/// <see cref="PhaseList.PatternRunway"/> ahead of <see cref="PhaseList.AssignedRunway"/> and waits for
/// the next cycle terminator to fly the transition. <c>FOLLOW</c> replaces the follower's phase list
/// outright — through the pattern join (<c>TryJoinLeadPattern</c>) and through the lead-landed final
/// sequence (<c>SequenceOntoFinal</c>) — and both rebuilds used to stamp the joined runway into both
/// fields, silently cancelling the armed clearance. The arming is carried over instead: the follow's
/// own circuit belongs to the lead's runway, the armed transition still applies after the terminator
/// that follows it. Joining the armed runway itself satisfies the arming, so both fields become it.
///
/// Real KOAK runways: 28R/28L are close parallels (no midfield crossing), 33 crosses them.
/// </summary>
[Collection("NavDbMutator")]
public class FollowArmedPatternRunwayTests
{
    private const string Lead = "LEAD";
    private const string Follower = "FOLL";

    public FollowArmedPatternRunwayTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // ---------------------------------------------------------------------------------------------
    // Pattern join — the lead is flying a circuit
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PatternJoin_ArmedParallel_FollowingALeadOnTheFlownRunway_KeepsTheArmedPatternRunway()
    {
        var navDb = TestVnasData.NavigationDb;
        var flown = navDb?.GetRunway("KOAK", "28R");
        var armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        var joined = JoinLeadPattern(leadRunway: flown, flownRunway: flown, armedRunway: armed, direction: PatternDirection.Right);

        Assert.Equal("28R", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28L", joined.Phases?.PatternRunway?.Designator);
        // 28R/28L are close parallels — nothing crosses the field — but the follower still leaves the
        // sequence it was just put into one terminator later, so the arming itself is announced.
        Assert.Contains(joined.PendingWarnings, w => w.Contains("28L stays armed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(joined.PendingWarnings, w => w.Contains("will leave the 28R sequence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(joined.PendingWarnings, w => w.Contains("cross midfield", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PatternJoin_ArmedRunwayIsTheLeadRunway_SatisfiesTheArmingOnBothFields()
    {
        var navDb = TestVnasData.NavigationDb;
        var flown = navDb?.GetRunway("KOAK", "28R");
        var armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        var joined = JoinLeadPattern(leadRunway: armed, flownRunway: flown, armedRunway: armed, direction: PatternDirection.Left);

        Assert.Equal("28L", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28L", joined.Phases?.PatternRunway?.Designator);
        Assert.DoesNotContain(joined.PendingWarnings, w => w.Contains("stays armed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A pattern runway armed at another airport is not this circuit's business: the follower is joining
    /// traffic here, and carrying it would arm a transition to a field it is not at.
    /// </summary>
    [Fact]
    public void PatternJoin_ArmedRunwayAtAnotherAirport_IsNotCarriedOver()
    {
        var navDb = TestVnasData.NavigationDb;
        var flown = navDb?.GetRunway("KOAK", "28R");
        var armedElsewhere = navDb?.GetRunway("KSFO", "28L");
        if (navDb is null || flown is null || armedElsewhere is null)
        {
            return;
        }

        var joined = JoinLeadPattern(leadRunway: flown, flownRunway: flown, armedRunway: armedElsewhere, direction: PatternDirection.Right);

        Assert.Equal("28R", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28R", joined.Phases?.PatternRunway?.Designator);
        Assert.DoesNotContain(joined.PendingWarnings, w => w.Contains("stays armed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A carried-over transition to a runway that is not a close parallel joins through a
    /// <see cref="MidfieldCrossingPhase"/>, which the RPO last heard about on a circuit the FOLLOW has
    /// now replaced — so it is announced (AIM 4-3-5, an unexpected maneuver in the pattern).
    /// </summary>
    [Fact]
    public void PatternJoin_ArmedCrossingRunway_WarnsThatTheCarriedTransitionCrossesTheField()
    {
        var navDb = TestVnasData.NavigationDb;
        var flown = navDb?.GetRunway("KOAK", "28R");
        var armed = navDb?.GetRunway("KOAK", "33");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        var joined = JoinLeadPattern(leadRunway: flown, flownRunway: flown, armedRunway: armed, direction: PatternDirection.Right);

        Assert.Equal("28R", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("33", joined.Phases?.PatternRunway?.Designator);
        Assert.Contains(joined.PendingWarnings, w => w.Contains("33 stays armed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(joined.PendingWarnings, w => w.Contains("cross midfield", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // Final sequence — the lead has landed and the follower is sequenced onto its runway
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void FinalSequence_ArmedParallel_AfterTheLeadLands_KeepsTheArmedPatternRunway()
    {
        var navDb = TestVnasData.NavigationDb;
        var flown = navDb?.GetRunway("KOAK", "28R");
        var armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        // Free (north) side of 28R and tracking away from the final course, so the airborne
        // final-join gates never fire and the lead-landed sequence is the only capture path.
        var lead = MakeVfr(Lead, OffFinal(flown, 0.8, 0), flown.TrueHeading, altitude: 300);
        lead.Phases = new PhaseList { AssignedRunway = flown, PatternRunway = flown };
        lead.Phases.Add(new FinalApproachPhase());

        var follower = MakeVfr(Follower, OffFinal(flown, 3.5, 0.6), new TrueHeading(112), altitude: 1200);
        follower.Approach.FollowingCallsign = Lead;
        var phase = new VfrFollowPhase(Lead);
        follower.Phases = new PhaseList
        {
            AssignedRunway = flown,
            PatternRunway = armed,
            TrafficDirection = PatternDirection.Right,
        };
        follower.Phases.Add(phase);

        var ctx = Ctx(follower, flown, cs => cs == Lead ? lead : null);
        follower.Phases.Start(ctx);

        // First tick captures the lead's landing runway while it is still airborne on final.
        phase.OnTick(ctx);
        Assert.IsType<VfrFollowPhase>(follower.Phases.CurrentPhase);

        lead.IsOnGround = true;
        phase.OnTick(ctx);

        Assert.IsType<PatternEntryPhase>(follower.Phases.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal("28L", follower.Phases.PatternRunway?.Designator);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("28L stays armed", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Places the follower on <paramref name="leadRunway"/>'s downwind abeam point (inside every join
    /// gate) behind a lead flying that runway's downwind, with the follower's outgoing phase list
    /// flying <paramref name="flownRunway"/> and <paramref name="armedRunway"/> armed. Returns the
    /// follower after the join tick.
    /// </summary>
    private static AircraftState JoinLeadPattern(RunwayInfo leadRunway, RunwayInfo flownRunway, RunwayInfo armedRunway, PatternDirection direction)
    {
        var airportRunways = NavigationDatabase.Instance.GetRunways(leadRunway.AirportId);
        var waypoints = PatternGeometry.Compute(
            leadRunway,
            AircraftCategory.Piston,
            "C172",
            windSpeedKt: 0,
            direction,
            sizeOverrideNm: null,
            altitudeOverrideFt: null,
            airportRunways,
            authoredRunway: null
        );

        var abeam = new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon);
        var lead = MakeVfr(Lead, GeoMath.ProjectPoint(abeam, waypoints.DownwindHeading, 0.5), waypoints.DownwindHeading, waypoints.PatternAltitude);
        lead.Phases = new PhaseList
        {
            AssignedRunway = leadRunway,
            PatternRunway = leadRunway,
            TrafficDirection = direction,
        };
        lead.Phases.Add(new DownwindPhase { Waypoints = waypoints });

        var follower = MakeVfr(Follower, abeam, waypoints.DownwindHeading, waypoints.PatternAltitude);
        follower.Approach.FollowingCallsign = Lead;
        follower.Pattern.TrafficDirection = direction;
        var phase = new VfrFollowPhase(Lead);
        follower.Phases = new PhaseList
        {
            AssignedRunway = flownRunway,
            PatternRunway = armedRunway,
            TrafficDirection = direction,
        };
        follower.Phases.Add(phase);

        var ctx = Ctx(follower, leadRunway, cs => cs == Lead ? lead : null);
        follower.Phases.Start(ctx);

        phase.OnTick(ctx);
        Assert.False(
            follower.Phases.CurrentPhase is VfrFollowPhase,
            "the follower never joined the lead's pattern — the join gates were not satisfied"
        );
        return follower;
    }

    private static AircraftState MakeVfr(string callsign, LatLon pos, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = pos,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState { HasReportedTrafficInSight = true },
        };

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, Func<string, AircraftState?> lookup) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            AircraftLookup = lookup,
            Logger = NullLogger.Instance,
        };

    /// <summary>Point <paramref name="alongNm"/> out the final approach course, <paramref name="crossNm"/> laterally
    /// (positive toward the runway heading's right-hand side).</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        var onCenterline = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), alongNm);
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        var perp = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perp, Math.Abs(crossNm));
    }
}

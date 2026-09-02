using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: a helicopter lead that has flown final and is now on
/// <see cref="HelicopterLandingPhase"/> — still airborne on the hover-descent to the spot
/// (<c>IsOnGround</c> stays false until it touches down) — must classify as pattern-flow-AHEAD
/// of a follower still on an earlier leg. <see cref="AirborneFollowHelper"/>'s
/// <c>PatternLegIndex</c> maps <see cref="LandingPhase"/>/<see cref="TouchAndGoPhase"/> to the
/// terminal leg 6 but OMITS <see cref="HelicopterLandingPhase"/>, so a landing helicopter reads
/// as leg <c>null</c> and both <c>IsLeadPatternFlowAhead</c> and <c>IsLeadPatternFlowBehind</c>
/// return false — dropping the #352 at-min-speed sequencing hold while the lead is still in the air.
///
/// Real KOAK 28R navdata; the lead's pattern is produced by the real <see cref="PatternBuilder"/>.
/// </summary>
[Collection("NavDbMutator")]
public class FollowHelicopterLandingLeadFlowAheadTests
{
    public FollowHelicopterLandingLeadFlowAheadTests() => TestVnasData.EnsureInitialized();

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

    private static AircraftState Make(string callsign, string type, LatLon pos, TrueHeading heading, double altitude, double ias) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pos,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState(),
        };

    [Fact]
    public void HelicopterLandingLead_StillAirborne_CountsAsFlowAhead()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy = navDb.GetRunway("KOAK", "28R");
        if (rwy is null)
        {
            return;
        }

        var allRunways = navDb.GetRunways("KOAK");
        const PatternDirection Dir = PatternDirection.Left;
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Piston, "", 0, Dir, null, null, allRunways, authoredRunway: null);

        Func<string, AircraftState?> lookup = _ => null;

        // Follower: a piston on the downwind leg (leg 3).
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        var followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 1.0);
        var follower = Make(FollowerCallsign, "C172", followerPos, wp.DownwindHeading, wp.PatternAltitude, 90);
        var followerCircuit = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Piston, "", 0, Dir, PatternEntryLeg.Downwind, false, null, null, null, allRunways, authoredRunway: null);
        follower.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = Dir, PatternRunway = rwy };
        foreach (var p in followerCircuit)
        {
            follower.Phases.Add(p);
        }
        follower.Phases.Start(Ctx(follower, rwy, lookup));
        Assert.IsType<DownwindPhase>(follower.Phases.CurrentPhase);

        // A real helicopter pattern terminates in HelicopterLandingPhase (not LandingPhase).
        var heliCircuit = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Helicopter, "", 0, Dir, PatternEntryLeg.Final, false, null, null, null, allRunways, authoredRunway: null);
        Assert.IsType<HelicopterLandingPhase>(heliCircuit[^1]);

        // Lead: the helicopter is now on that terminal landing phase, still airborne over the
        // threshold on the hover-descent (IsOnGround only flips at agl <= 0).
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var lead = Make(LeadCallsign, "EC30", threshold, wp.FinalHeading, rwy.ElevationFt + 30, 20);
        lead.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = Dir, PatternRunway = rwy };
        lead.Phases.Add(new HelicopterLandingPhase());
        lead.Phases.Start(Ctx(lead, rwy, lookup));
        Assert.IsType<HelicopterLandingPhase>(lead.Phases.CurrentPhase);
        Assert.False(lead.IsOnGround, "Helicopter is still airborne on the landing flare.");

        // The lead is landing — maximally pattern-flow-ahead of a downwind follower.
        Assert.True(
            AirborneFollowHelper.IsLeadPatternFlowAhead(follower, lead),
            "A helicopter lead still airborne on HelicopterLandingPhase must count as flow-ahead (terminal leg 6)."
        );
    }

    private const string LeadCallsign = "N100AA";
    private const string FollowerCallsign = "N200BB";
}

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
/// Regression: a lead on any of the phases a pattern circuit can END on must classify as
/// pattern-flow-AHEAD of a follower still on an earlier leg. <see cref="AirborneFollowHelper"/>'s
/// <c>PatternLegIndex</c> mapped only <see cref="LandingPhase"/> and <see cref="TouchAndGoPhase"/>
/// to the terminal leg 6, omitting <see cref="HelicopterLandingPhase"/> (a helicopter circuit's own
/// terminal, still airborne on the hover-descent — <c>IsOnGround</c> stays false until touchdown)
/// and the two clearance swaps <c>PatternCommandHandler.OptionClearanceTerminal</c> installs through
/// <c>ReplaceApproachEnding</c>, <see cref="StopAndGoPhase"/> and <see cref="LowApproachPhase"/>.
/// An omitted terminal reads as leg <c>null</c>, which makes both <c>IsLeadPatternFlowAhead</c> and
/// <c>IsLeadPatternFlowBehind</c> return false — dropping the #352 at-min-speed sequencing hold
/// while the lead is still on the runway.
///
/// Real KOAK 28R navdata; the follower's pattern is produced by the real <see cref="PatternBuilder"/>.
/// </summary>
[Collection("NavDbMutator")]
public class FollowPatternTerminalLeadFlowAheadTests
{
    private const string LeadCallsign = "N100AA";
    private const string FollowerCallsign = "N200BB";

    public FollowPatternTerminalLeadFlowAheadTests() => TestVnasData.EnsureInitialized();

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

    /// <summary>
    /// A piston follower established on the downwind leg (index 3) of KOAK 28R's left pattern,
    /// together with the runway and geometry its lead must share.
    /// </summary>
    private static (
        AircraftState Follower,
        RunwayInfo Runway,
        PatternWaypoints Waypoints,
        Func<string, AircraftState?> Lookup
    ) BuildDownwindFollower()
    {
        var navDb = TestVnasData.NavigationDb;
        Assert.NotNull(navDb);

        var rwy = navDb.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy);

        var allRunways = navDb.GetRunways("KOAK");
        const PatternDirection Dir = PatternDirection.Left;
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Piston, "", 0, Dir, null, null, allRunways, authoredRunway: null);

        Func<string, AircraftState?> lookup = _ => null;

        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        var followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 1.0);
        var follower = Make(FollowerCallsign, "C172", followerPos, wp.DownwindHeading, wp.PatternAltitude, 90);
        var followerCircuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Piston,
            "",
            0,
            Dir,
            PatternEntryLeg.Downwind,
            false,
            null,
            null,
            null,
            allRunways,
            authoredRunway: null
        );
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        foreach (var p in followerCircuit)
        {
            follower.Phases.Add(p);
        }
        follower.Phases.Start(Ctx(follower, rwy, lookup));
        Assert.IsType<DownwindPhase>(follower.Phases.CurrentPhase);

        return (follower, rwy, wp, lookup);
    }

    /// <summary>Places a lead over the threshold of the follower's runway on <paramref name="terminal"/>.</summary>
    private static AircraftState BuildTerminalLead(
        Phase terminal,
        string aircraftType,
        RunwayInfo rwy,
        PatternWaypoints wp,
        Func<string, AircraftState?> lookup
    )
    {
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var lead = Make(LeadCallsign, aircraftType, threshold, wp.FinalHeading, rwy.ElevationFt + 30, 20);
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = PatternDirection.Left,
            PatternRunway = rwy,
        };
        lead.Phases.Add(terminal);
        lead.Phases.Start(Ctx(lead, rwy, lookup));
        return lead;
    }

    [Fact]
    public void HelicopterLandingLead_StillAirborne_CountsAsFlowAhead()
    {
        var (follower, rwy, wp, lookup) = BuildDownwindFollower();

        var navDb = TestVnasData.NavigationDb;
        Assert.NotNull(navDb);
        var allRunways = navDb.GetRunways("KOAK");

        // A real helicopter pattern terminates in HelicopterLandingPhase (not LandingPhase).
        var heliCircuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Helicopter,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Final,
            false,
            null,
            null,
            null,
            allRunways,
            authoredRunway: null
        );
        Assert.IsType<HelicopterLandingPhase>(heliCircuit[^1]);

        // Lead: the helicopter is now on that terminal landing phase, still airborne over the
        // threshold on the hover-descent (IsOnGround only flips at agl <= 0).
        var lead = BuildTerminalLead(new HelicopterLandingPhase(), "EC30", rwy, wp, lookup);
        Assert.IsType<HelicopterLandingPhase>(lead.Phases!.CurrentPhase);
        Assert.False(lead.IsOnGround, "Helicopter is still airborne on the landing flare.");

        // The lead is landing — maximally pattern-flow-ahead of a downwind follower.
        Assert.True(
            AirborneFollowHelper.IsLeadPatternFlowAhead(follower, lead),
            "A helicopter lead still airborne on HelicopterLandingPhase must count as flow-ahead (terminal leg 6)."
        );
    }

    [Theory]
    [InlineData("landing")]
    [InlineData("touch-and-go")]
    [InlineData("stop-and-go")]
    [InlineData("low-approach")]
    public void EveryPatternTerminalLead_CountsAsFlowAhead(string terminalName)
    {
        var (follower, rwy, wp, lookup) = BuildDownwindFollower();

        Phase terminal = terminalName switch
        {
            "landing" => new LandingPhase(),
            "touch-and-go" => new TouchAndGoPhase(),
            "stop-and-go" => new StopAndGoPhase(),
            "low-approach" => new LowApproachPhase(),
            _ => throw new ArgumentOutOfRangeException(nameof(terminalName), terminalName, "Unknown terminal"),
        };

        var lead = BuildTerminalLead(terminal, "C172", rwy, wp, lookup);

        Assert.True(
            AirborneFollowHelper.IsLeadPatternFlowAhead(follower, lead),
            $"A lead on the {terminalName} terminal must count as flow-ahead of a downwind follower (terminal leg 6)."
        );
    }
}

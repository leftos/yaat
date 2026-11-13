using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A pursuit follower behind a lead that lands and then holds in position on the runway reaches <see cref="FinalApproachPhase"/> —
/// joined onto the lead's final while it is airborne, sequenced onto the landed runway's final, or with the follow ended and the
/// pattern re-entered — where <see cref="OccupiedRunwayGoAround.TryTrigger"/> sends it around. A follower too close to join the
/// landed lead's final never touches down on the occupied runway. Real OAK navdata, C172s on 28R.
/// </summary>
public class FollowOccupiedRunwayGoAroundTests(ITestOutputHelper output)
{
    /// <summary>The callsign <see cref="FollowKeepApproachTests.AddFinalLead"/> gives its lead.</summary>
    private const string Leader = "LEAD1";

    private const string Follower = "FOLL1";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("OccupiedRunwayGoAround", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("PatternEntryPhase", LogLevel.Debug)
            .EnableCategory("FinalApproachPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// A pursuit follower trailing the lead by more than the in-trail floor joins its final while it is still airborne
    /// (<c>TryJoinLeadFinal</c>), and goes around short of the threshold once the landed lead holds in position there.
    /// </summary>
    [Fact]
    public void PursuerJoinedLeadFinal_GoesAroundWhileLeadHoldsOnRunway()
    {
        SimulationEngine engine = BuildEngine();
        Attachment(engine);
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddLandedLead(engine, 1.0);
        AircraftState follower = AddPursuer(engine, 3.0, 1000);

        engine.TickPhysics(0.25);
        Assert.True(
            follower.Phases!.CurrentPhase is PatternEntryPhase or FinalApproachPhase,
            $"the follower did not join the lead's final ({State(follower, rwy)})"
        );
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);

        TickUntil(engine, 2400, () => lead.IsOnGround, () => $"the lead never touched down ({State(lead, rwy)})");
        AssertGoesAround(engine, lead, follower, rwy);
    }

    /// <summary>
    /// A pursuit follower inside the in-trail floor is in a spacing excursion when the lead lands, so the follow ends and the
    /// pattern it re-enters never puts it down on the occupied runway: with the landed lead holding in position it goes around and
    /// never touches down in 300 s.
    /// </summary>
    [Fact]
    public void PursuerSequencedBehindLandedLead_NeverTouchesDownWhileLeadHoldsOnRunway()
    {
        SimulationEngine engine = BuildEngine();
        Attachment(engine);
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddLandedLead(engine, 0.6);
        AircraftState follower = AddPursuer(engine, 1.8, 700);

        TickUntil(engine, 2400, () => lead.IsOnGround, () => $"the lead never touched down ({State(lead, rwy)})");
        PinOnRunway(lead, rwy, 1500);

        List<string> phases = FollowWhileLeadHoldsInPosition(engine, follower, rwy);

        output.WriteLine($"follower phases: {string.Join(" > ", phases)}");
        Assert.Contains("GoAroundPhase", phases);
    }

    /// <summary>
    /// A follower too close to join the landed lead's final when the lead lands never touches down while the lead holds in
    /// position: the follow ends and whatever final it comes back on goes around when it reaches the occupied runway.
    /// </summary>
    [Fact]
    public void TooClosePursuer_LeadLandedInsideTheJoinMinimum_NeverTouchesDownWhileLeadHoldsOnRunway()
    {
        SimulationEngine engine = BuildEngine();
        Attachment(engine);
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddLandedLead(engine, 0.3);
        AircraftState follower = AddPursuer(engine, 1.0, 600);

        TickUntil(engine, 1200, () => lead.IsOnGround, () => $"the lead never touched down ({State(lead, rwy)})");
        PinOnRunway(lead, rwy, 1500);
        output.WriteLine($"at the pin: follower {State(follower, rwy)}");

        List<string> phases = FollowWhileLeadHoldsInPosition(engine, follower, rwy);

        output.WriteLine($"follower phases: {string.Join(" > ", phases)}");
        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    /// <summary>
    /// Pins the landed lead holding in position 1,500 ft down the 28R centreline and asserts the follower goes around before
    /// touching down, telling the instructor which aircraft blocks the runway.
    /// </summary>
    private static void AssertGoesAround(SimulationEngine engine, AircraftState lead, AircraftState follower, RunwayInfo rwy)
    {
        PinOnRunway(lead, rwy, 1500);

        TickUntil(
            engine,
            2400,
            () => (follower.Phases?.CurrentPhase is GoAroundPhase) || follower.IsOnGround,
            () => $"the follower never went around for the holding-in-position lead ({State(follower, rwy)})"
        );
        Assert.IsType<GoAroundPhase>(follower.Phases!.CurrentPhase);
        Assert.False(follower.IsOnGround, $"the follower touched down on the occupied runway ({State(follower, rwy)})");
        Assert.Contains(
            follower.PendingWarnings,
            w =>
                w.Contains("go-around:", StringComparison.Ordinal)
                && w.Contains(Leader, StringComparison.Ordinal)
                && w.Contains("28R", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Ticks 300 s while the lead holds in position, recording each distinct follower phase and failing on the first tick the
    /// follower touches down. Returns the phase sequence for the test's log.
    /// </summary>
    private static List<string> FollowWhileLeadHoldsInPosition(SimulationEngine engine, AircraftState follower, RunwayInfo rwy)
    {
        var phases = new List<string>();
        for (int i = 0; i < 1200; i++)
        {
            engine.TickPhysics(0.25);
            string? phase = follower.Phases?.CurrentPhase?.GetType().Name;
            if ((phase is not null) && ((phases.Count == 0) || (phases[^1] != phase)))
            {
                phases.Add(phase);
            }

            if (follower.IsOnGround)
            {
                Assert.Fail(
                    $"the follower touched down on the occupied runway after {i} ticks ({State(follower, rwy)}; "
                        + $"phases {string.Join(" > ", phases)})"
                );
            }
        }

        return phases;
    }

    /// <summary>The lead on the 28R final <paramref name="alongNm"/> out, cleared to land on 28R.</summary>
    private static AircraftState AddLandedLead(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = FollowKeepApproachTests.AddFinalLead(engine, rwy, alongNm);
        lead.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        lead.Phases.ClearedRunwayId = rwy.Designator;
        return lead;
    }

    /// <summary>
    /// A C172 pursuing <see cref="Leader"/> at <paramref name="alongNm"/> out on the 28R centerline, flying the 28R heading at
    /// <paramref name="altitudeFt"/>, cleared to land and returning to the 28R right-traffic circuit when the follow ends.
    /// </summary>
    private static AircraftState AddPursuer(SimulationEngine engine, double alongNm, double altitudeFt)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, altitudeFt);
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList { LandingClearance = ClearanceType.ClearedToLand, ClearedRunwayId = rwy.Designator };
        follower.Approach.HasReportedTrafficInSight = true;
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            Leader,
            new FollowPatternReturn(rwy, PatternDirection.Right, PatternAltitudeFt(rwy), false),
            climbOutGate: null
        );
        return follower;
    }

    /// <summary>
    /// Stops <paramref name="lead"/> on <paramref name="rwy"/>'s centreline <paramref name="pastThresholdFt"/> past the threshold,
    /// holding in position.
    /// </summary>
    private static void PinOnRunway(AircraftState lead, RunwayInfo rwy, double pastThresholdFt)
    {
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        lead.Position = GeoMath.ProjectPoint(threshold, rwy.TrueHeading, pastThresholdFt / GeoMath.FeetPerNm);
        lead.TrueHeading = rwy.TrueHeading;
        lead.TrueTrack = rwy.TrueHeading;
        lead.Altitude = rwy.ElevationFt;
        lead.IndicatedAirspeed = 0;
        lead.IsOnGround = true;
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new HoldingInPositionPhase());
        lead.Phases.CurrentPhase!.Status = PhaseStatus.Active;
    }

    /// <summary>
    /// The room scenario the occupied-runway go-around setting comes from: KOAK loaded, the go-around on, towered airports at
    /// their default.
    /// </summary>
    private static void Attachment(SimulationEngine engine) =>
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "follow-occupied-runway",
            ScenarioName = "follow-occupied-runway",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoGoAroundOnOccupiedRunway = true,
        };

    /// <summary>Ticks 0.25 s at a time until <paramref name="done"/> holds, failing with <paramref name="describe"/> when it never does.</summary>
    private static void TickUntil(SimulationEngine engine, int maxTicks, Func<bool> done, Func<string> describe)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            engine.TickPhysics(0.25);
            if (done())
            {
                return;
            }
        }

        Assert.Fail($"hung: {describe()} after {maxTicks} ticks");
    }

    /// <summary>
    /// <paramref name="ac"/>'s phase, follow target, distance from the 28R threshold and height above the field, for a failure
    /// message.
    /// </summary>
    private static string State(AircraftState ac, RunwayInfo rwy) =>
        $"phase {ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}, following {ac.Approach.FollowingCallsign ?? "(none)"}, "
        + $"{AlongFinalNm(rwy, ac.Position):F2} nm from the 28R threshold, {ac.Altitude - rwy.ElevationFt:F0} ft AGL";

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        LatLon onCenterline = GeoMath.ProjectPoint(threshold, rwy.TrueHeading.ToReciprocal(), alongNm);
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    private static double AlongFinalNm(RunwayInfo rwy, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal());

    private static double PatternAltitudeFt(RunwayInfo rwy) =>
        rwy.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Piston);

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston),
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };
}

using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Ground-conflict priority across a runway takes the pilot rule (AIM 2-3-5.a.1, 4-3-21.b; YAAT-402): a runway user first, an
/// aircraft still vacating a runway — its tail over the pavement or inside the hold line — next, everyone else last. The rank decides
/// only who gives way; a vacating aircraft keeps its own closing check against what lies in its path. Real layouts and runways only.
/// </summary>
public class GroundConflictRunwayRankTests
{
    private static readonly TrueHeading Runway28RHeading = new(292.2);
    private static readonly LatLon OakPBranch = new(37.729433, -122.219017);

    public GroundConflictRunwayRankTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // --- Placed on real layouts -----------------------------------------------------------------

    /// <summary>
    /// B6 / YAAT-825: a B77W stopped on an SFO 28R high-speed exit, tail over the strict pavement 418 ft along the runway from the
    /// nearest bar, is vacating 28R; a B738 taxiing at it from the side, not in its path, yields to it.
    /// </summary>
    [Fact]
    public void TailOverThePavementFarFromAnyBar_TaxiTrafficFromTheSideYields()
    {
        if (ExitPose() is not { } pose)
        {
            return;
        }

        AircraftState exiting = pose.Stopped();
        AircraftState taxiing = Converging("TAXI1", exiting, offNoseDeg: -60.0, distanceFt: 260.0, new TaxiingPhase(), runway: null);

        List<string> lines = Resolve(pose.Layout, exiting, taxiing);

        Assert.Equal(0.0, taxiing.Ground.SpeedLimit);
        Assert.Contains(lines, line => line.Contains("[Crossing] EXIT1 vacating runway → TAXI1 yields", StringComparison.Ordinal));
        Assert.Null(exiting.Ground.SpeedLimit);
    }

    /// <summary>
    /// Owner ruling (2026-10-18): an aircraft lining up on 28R whose main gear is not yet over the strict pavement is not a runway
    /// user, so it gives way to an aircraft coming off 28R with its tail still on the pavement.
    /// </summary>
    [Fact]
    public void LineUpBeforeThePavement_YieldsToAVacatingExit()
    {
        if (ExitPose() is not { } pose)
        {
            return;
        }

        AircraftState exiting = pose.Stopped();
        AircraftState liningUp = Converging("LUP1", exiting, offNoseDeg: -60.0, distanceFt: 260.0, new LineUpPhase(), pose.Runway);
        Assert.False(RunwayOccupancy.IsOverStrictPavement(liningUp.Position, pose.Runway));

        List<string> lines = Resolve(pose.Layout, exiting, liningUp);

        Assert.Equal(0.0, liningUp.Ground.SpeedLimit);
        Assert.Contains(lines, line => line.Contains("[Crossing] EXIT1 vacating runway → LUP1 yields", StringComparison.Ordinal));
        Assert.Null(exiting.Ground.SpeedLimit);
    }

    /// <summary>
    /// Owner ruling (2026-10-18, C1): a vacating aircraft that is held (stopped under a controller hold) gets right of way only
    /// against traffic coming head-on into its way out. A B738 taxiing at the held B77W from 60° off its nose, outside the in-path
    /// corridor, is not frozen by the vacating rule: the pair takes the normal closing check.
    /// </summary>
    [Fact]
    public void HeldVacatingExit_TaxiTrafficFromTheSide_TakesTheNormalClosingCheck()
    {
        if (ExitPose() is not { } pose)
        {
            return;
        }

        AircraftState exiting = pose.Stopped();
        exiting.Ground.Hold = HoldDirective.HoldPosition;
        AircraftState taxiing = Converging("TAXI1", exiting, offNoseDeg: -60.0, distanceFt: 260.0, new TaxiingPhase(), runway: null);

        List<string> lines = Resolve(pose.Layout, exiting, taxiing);

        Assert.DoesNotContain(lines, line => line.Contains("vacating runway", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("TAXI1(dir=", StringComparison.Ordinal) && line.Contains("→limit=", StringComparison.Ordinal));
        Assert.NotEqual(0.0, taxiing.Ground.SpeedLimit);
    }

    /// <summary>
    /// B3: a departure rolling down 28R 150 ft behind abeam a stopped vacating arrival takes no limit from it — runway users rank
    /// above vacating aircraft (7110.65 3-1-3), and a ground-conflict limit on the ground is an instant speed clamp.
    /// </summary>
    [Fact]
    public void DepartureRollingPastAStoppedVacatingExit_GetsNoLimit()
    {
        if (ExitPose() is not { } pose)
        {
            return;
        }

        AircraftState exiting = pose.Stopped();
        var end1 = new LatLon(pose.Runway.Lat1, pose.Runway.Lon1);
        var end2 = new LatLon(pose.Runway.Lat2, pose.Runway.Lon2);
        LatLon abeam = GeoMath.FootOfPerpendicular(exiting.Position, end1, end2).Foot;
        LatLon behind = Project(abeam, pose.Runway.TrueHeading.ToReciprocal(), 150.0);
        AircraftState departure = Ground("DEP1", "B738", behind, pose.Runway.TrueHeading, 60.0, new TakeoffPhase(), pose.Runway);

        Resolve(pose.Layout, exiting, departure);

        Assert.Null(departure.Ground.SpeedLimit);
    }

    /// <summary>
    /// B4: a vacating aircraft keeps its own closing check against an aircraft stopped ahead on its exit — its priority makes others
    /// give way; it does not let it drive into what is in its path (AIM 4-3-21.b).
    /// </summary>
    [Fact]
    public void VacatingExit_KeepsItsClosingLimit_AgainstAnAircraftStoppedAheadOnItsExit()
    {
        if (ExitPose() is not { } pose)
        {
            return;
        }

        AircraftState exiting = pose.Stopped();
        exiting.IndicatedAirspeed = 10.0;
        LatLon ahead = Project(exiting.Position, exiting.TrueHeading, 260.0);
        AircraftState stopped = Ground("STOP1", "B738", ahead, exiting.TrueHeading, 0.0, new TaxiingPhase(), runway: null);

        Resolve(pose.Layout, exiting, stopped);

        Assert.True(
            exiting.Ground.SpeedLimit is <= 10.0,
            $"the vacating exit was limited to {exiting.Ground.SpeedLimit?.ToString("F1") ?? "nothing"}"
        );
    }

    /// <summary>
    /// B9: the detector runs on the primary airport's layout (OAK). At another airport (HWD) the phase alone ranks the aircraft, as
    /// before: a line-up on HWD 28L, short of its pavement, keeps its priority over HWD taxi traffic.
    /// </summary>
    [Fact]
    public void OtherAirport_LineUpKeepsItsPhasePriority()
    {
        AirportGroundLayout? oak = new TestAirportGroundData().GetLayout("OAK");
        if ((oak is null) || (RunwayBarFixture.RunwayBar("KHWD", "28L") is not { } hwd))
        {
            return;
        }

        AircraftState liningUp = RunwayOccupancyTests.SquareToTheMarking(hwd.Runway, hwd.Bar, "B738", facingRunway: true, 0.0, 0.0);
        liningUp.Callsign = "LUP1";
        liningUp.AirportId = "HWD";
        liningUp.IndicatedAirspeed = 5.0;
        Attach(liningUp, new LineUpPhase(), hwd.Runway);
        AircraftState taxiing = Converging("TAXI1", liningUp, offNoseDeg: 60.0, distanceFt: 200.0, new TaxiingPhase(), runway: null);

        List<string> lines = Resolve(oak, liningUp, taxiing);

        Assert.Equal(0.0, taxiing.Ground.SpeedLimit);
        Assert.Contains(lines, line => line.Contains("[Crossing] LUP1 on runway → TAXI1 yields", StringComparison.Ordinal));
    }

    // --- A real committed exit (OAK 28R, P to J) ----------------------------------------------------

    /// <summary>
    /// B1: an OAK 28R arrival exiting on P toward J's bar (P meets J inside the 28R holding distance) keeps priority over traffic on
    /// J while its tail is inside the hold line, though it has left the centreline (<see cref="RunwayExitPhase.IsOnCenterline"/>
    /// false, following its exit path).
    /// </summary>
    [Fact]
    public void PExit_TailInsideTheHoldLine_JTrafficYields()
    {
        if (ExitingOnP() is not { } exit)
        {
            return;
        }

        List<string> lines = Resolve(exit.Layout, exit.Arrival, exit.JTraffic);

        Assert.True(exit.JTraffic.Ground.SpeedLimit == 0.0, $"J traffic limit {exit.JTraffic.Ground.SpeedLimit}: " + string.Join(" | ", lines));
        Assert.Contains(lines, line => line.Contains("[Crossing] TST1 vacating runway → JTFC1 yields", StringComparison.Ordinal));
        Assert.Null(exit.Arrival.Ground.SpeedLimit);
    }

    /// <summary>
    /// B7 / B12: the rank comes only from state a snapshot restores — the arrival restored mid-exit, before its phase rebuilds its
    /// exit route, gives J traffic the same limits as the live one.
    /// </summary>
    [Fact]
    public void PExit_SnapshotRoundTripMidExit_GivesTheSameLimits()
    {
        if (ExitingOnP() is not { } exit)
        {
            return;
        }

        var restored = AircraftState.FromSnapshot(exit.Arrival.ToSnapshot(), exit.Layout);
        var restoredTraffic = AircraftState.FromSnapshot(exit.JTraffic.ToSnapshot(), exit.Layout);

        Resolve(exit.Layout, exit.Arrival, exit.JTraffic);
        Resolve(exit.Layout, restored, restoredTraffic);

        Assert.Equal(exit.JTraffic.Ground.SpeedLimit, restoredTraffic.Ground.SpeedLimit);
        Assert.Equal(exit.JTraffic.Ground.AutoYieldTarget, restoredTraffic.Ground.AutoYieldTarget);
        Assert.Equal(exit.Arrival.Ground.SpeedLimit, restored.Ground.SpeedLimit);
        Assert.Equal(0.0, restoredTraffic.Ground.SpeedLimit);
    }

    private const double ArrivalWithinBarFt = 120.0;
    private const double JTrafficBeyondBarFt = 60.0;

    private sealed record PExit(AirportGroundLayout Layout, AircraftState Arrival, AircraftState JTraffic);

    /// <summary>
    /// A C25A landed on OAK 28R and exiting on P, ticked until it follows its exit path with its main gear off the pavement and its
    /// tail still inside the hold line within <see cref="ArrivalWithinBarFt"/> of J's 28R bar, and a B738 on J beyond that bar
    /// taxiing toward it (<see cref="JTrafficBeyond"/>). Null when the data is unavailable.
    /// </summary>
    private static PExit? ExitingOnP()
    {
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "C25A", "TST1", 1.0);
        if (spawned is null)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState arrival, RunwayInfo runway) = spawned;
        AirportGroundLayout layout = arrival.Ground.Layout!;
        GroundNode centreline = layout.FindNearestCenterlineNode(OakPBranch.Lat, OakPBranch.Lon, Runway28RHeading, "28R")!;
        var pRight = new ExitPreference { Taxiway = "P", Side = ExitSide.Right };
        GroundNode jBar = layout.FindAdjacentHoldShort(centreline, "28R", Runway28RHeading, pRight)!.Value.Node;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            bool offTheCentreline = arrival.Phases?.CurrentPhase is RunwayExitPhase { IsOnCenterline: false };
            bool nearTheBar = (GeoMath.DistanceNm(arrival.Position, jBar.Position) * GeoMath.FeetPerNm) <= ArrivalWithinBarFt;
            bool offThePavement = !RunwayOccupancy.IsOnPavement(arrival, runway);
            if (offTheCentreline && nearTheBar && offThePavement && TailInside(arrival, runway, jBar))
            {
                return new PExit(layout, arrival, JTrafficBeyond(jBar, runway));
            }
        }

        Assert.Fail("the arrival never left the 28R centreline on P with its tail inside the hold line within 300 s");
        return null;
    }

    /// <summary>
    /// Measured apart from the predicate under test: the arrival's tail (half its length behind the main gear) lies over 28R's
    /// strict paved half-width, or more than <see cref="RunwayOccupancy.HoldLineFoulToleranceFt"/> nearer the centreline than J's bar.
    /// </summary>
    private static bool TailInside(AircraftState arrival, RunwayInfo runway, GroundNode jBar)
    {
        LatLon tail = AircraftBody.Of(arrival.AircraftType).PointOf(AircraftBodyPart.Tail, arrival.Position, arrival.TrueHeading.Degrees);
        double tailFt = RunwayHoldBars.CentrelineFt(runway, tail);
        double barFt = RunwayHoldBars.CentrelineFt(runway, jBar.Position);
        return (tailFt <= (runway.WidthFt / 2.0)) || (tailFt < (barFt - RunwayOccupancy.HoldLineFoulToleranceFt));
    }

    /// <summary>
    /// A B738 on J <see cref="JTrafficBeyondBarFt"/> beyond <paramref name="jBar"/>, away from the runway along J's first edge
    /// there, taxiing toward the bar at 10 kt.
    /// </summary>
    private static AircraftState JTrafficBeyond(GroundNode jBar, RunwayInfo runway)
    {
        GroundNode beyond = jBar
            .Edges.Where(edge => edge.MatchesTaxiway("J"))
            .Select(edge => edge.Nodes[0].Id == jBar.Id ? edge.Nodes[1] : edge.Nodes[0])
            .First(node => RunwayHoldBars.CentrelineFt(runway, node.Position) > RunwayHoldBars.CentrelineFt(runway, jBar.Position));
        LatLon position = Project(jBar.Position, new TrueHeading(GeoMath.BearingTo(jBar.Position, beyond.Position)), JTrafficBeyondBarFt);
        var towardBar = new TrueHeading(GeoMath.BearingTo(position, jBar.Position));
        return Ground("JTFC1", "B738", position, towardBar, 10.0, new TaxiingPhase(), runway: null);
    }

    // --- Clearing one runway toward its parallel (SFO 28R / 28L) ----------------------------------

    /// <summary>
    /// C2: an aircraft pulling forward off SFO 28R (<see cref="ClearRunwayPhase"/>) is leaving 28R only. Stopped past 28R's
    /// marking, facing 28L with its tail 10 ft inside 28L's hold line, it is approaching 28L, not vacating it, so it gains no
    /// priority over taxi traffic from the side.
    /// </summary>
    [Fact]
    public void ClearingRunway28R_TailInside28LsHoldLine_IsApproaching28LNotVacatingIt()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("KSFO");
        RunwayInfo? r28R = NavigationDatabase.Instance.GetRunway("KSFO", "28R");
        RunwayInfo? r28L = NavigationDatabase.Instance.GetRunway("KSFO", "28L");
        if ((layout is null) || (r28R is null) || (r28L is null))
        {
            return;
        }

        bool rightOf28L = RunwayHoldBars.IsRightOfCentreline(r28L, new LatLon(r28R.Lat1, r28R.Lon1));
        var end1 = new LatLon(r28L.Lat1, r28L.Lon1);
        var end2 = new LatLon(r28L.Lat2, r28L.Lon2);
        GroundNode bar = RunwayHoldBars
            .RunwayBars(layout, r28L)
            .Where(b =>
                (RunwayHoldBars.IsRightOfCentreline(r28L, b.Position) == rightOf28L) && !GeoMath.FootOfPerpendicular(b.Position, end1, end2).Clamped
            )
            .OrderBy(b => b.Id)
            .First();
        AircraftState clearing = RunwayOccupancyTests.SquareToTheMarking(
            r28L,
            bar,
            "B738",
            facingRunway: true,
            AircraftLength.ResolveFt("B738") + 10.0,
            0.0
        );
        clearing.AirportId = "SFO";
        GroundNode on28R = layout.FindNearestCenterlineNode(clearing.Position.Lat, clearing.Position.Lon, r28R.TrueHeading, "28R")!;
        Attach(clearing, new ClearRunwayPhase(on28R.Id, on28R.Id), r28R);
        var body = AircraftBody.Of("B738");
        Assert.False(RunwayOccupancy.IsVacating(clearing, body, r28R, layout), "the pose must be past 28R's marking");
        AircraftState taxiing = Converging("TAXI1", clearing, offNoseDeg: 90.0, distanceFt: 200.0, new TaxiingPhase(), runway: null);

        bool vacating28L = RunwayOccupancy.IsVacating(clearing, body, r28L, layout);
        List<string> lines = Resolve(layout, clearing, taxiing);

        Assert.False(vacating28L);
        Assert.DoesNotContain(lines, line => line.Contains("vacating runway", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains("FOUL1(dir=", StringComparison.Ordinal) && line.Contains("TAXI1(dir=", StringComparison.Ordinal)
        );
    }

    // --- The YAAT-318 rig: a holder past OAK 33's line ----------------------------------------------

    /// <summary>
    /// B2: a holder at OAK 33's bar facing away from the runway with its tail 20 ft inside the line would read as leaving by its
    /// pose; its <see cref="HoldingShortPhase"/> at the bar makes it approaching, so it gains no priority. Taxi traffic coming
    /// head-on along its heading takes the normal treatment with it.
    /// </summary>
    [Fact]
    public void HolderFacingAwayWithItsTailInsideTheRunway33Line_GainsNoPriority()
    {
        if (RunwayBarFixture.RunwayBar("KOAK", "33") is not { } oak)
        {
            return;
        }

        var holdShort = new HoldShortPoint
        {
            NodeId = oak.Bar.Id,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "33",
        };
        AircraftState holder = RunwayOccupancyTests.SquareToTheMarking(oak.Runway, oak.Bar, "B738", facingRunway: false, 20.0, 0.0);
        Attach(holder, new HoldingShortPhase(holdShort), oak.Runway);
        var body = AircraftBody.Of("B738");
        double tailFt = RunwayHoldBars.CentrelineFt(oak.Runway, body.PointOf(AircraftBodyPart.Tail, holder.Position, holder.TrueHeading.Degrees));
        double barFt = RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position);
        Assert.True(tailFt < (barFt - RunwayOccupancy.HoldLineFoulToleranceFt), $"tail {tailFt:F1} ft, bar {barFt:F1} ft from the centreline");
        AircraftState taxiing = Converging("TAXI1", holder, offNoseDeg: 0.0, distanceFt: 220.0, new TaxiingPhase(), runway: null);

        List<string> lines = Resolve(oak.Layout, holder, taxiing);

        Assert.False(RunwayOccupancy.IsVacating(holder, body, oak.Runway, oak.Layout));
        Assert.DoesNotContain(lines, line => line.Contains("vacating runway", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains("FOUL1(dir=", StringComparison.Ordinal) && line.Contains("TAXI1(dir=", StringComparison.Ordinal)
        );
    }

    // --- A HOLD mid-crossing (the N70CS recording, OAK 28R on J) ----------------------------------

    /// <summary>
    /// B5, near side: N70CS, cleared across OAK 28R on J and held the moment its main gear is over the pavement on the near half,
    /// is still vacating 28R: taxi traffic coming head-on into its way out gives way to it.
    /// </summary>
    [Fact]
    public void N70csHeldOnTheNearHalfOf28R_HeadOnTrafficYields()
    {
        if (N70csHeld(CrossingStop.NearHalf) is not { } held)
        {
            return;
        }

        Assert.True(RunwayOccupancy.IsOverStrictPavement(held.Crosser.Position, held.Runway), "N70CS should be stopped on the pavement");
        Assert.False(OnFarSide(held), "N70CS should be stopped on the near half");
        AircraftState taxiing = HeadOn(held.Crosser);

        List<string> lines = Resolve(held.Layout, held.Crosser, taxiing);

        Assert.Equal(0.0, taxiing.Ground.SpeedLimit);
        Assert.Contains(lines, line => line.Contains("[Crossing] N70CS vacating runway → TAXI1 yields", StringComparison.Ordinal));
    }

    /// <summary>B5, far side: N70CS held once its main gear is over the far half of 28R is vacating it the same way.</summary>
    [Fact]
    public void N70csHeldOnTheFarHalfOf28R_HeadOnTrafficYields()
    {
        if (N70csHeld(CrossingStop.FarHalf) is not { } held)
        {
            return;
        }

        Assert.True(OnFarSide(held), "N70CS should be stopped on the far half");
        AircraftState taxiing = HeadOn(held.Crosser);

        List<string> lines = Resolve(held.Layout, held.Crosser, taxiing);

        Assert.Equal(0.0, taxiing.Ground.SpeedLimit);
        Assert.Contains(lines, line => line.Contains("[Crossing] N70CS vacating runway → TAXI1 yields", StringComparison.Ordinal));
    }

    /// <summary>
    /// F1: a crossing whose target is a plain route node rather than a hold bar — what the crossing's fallback resolves when the
    /// route carries only one annotated hold-short (the route's first node past the near bar) — is still a crossing of 28R, named
    /// by its runway. N70CS held on the near half of the pavement with such a crossing ranks as vacating.
    /// </summary>
    [Fact]
    public void N70csHeldOnTheNearHalf_CrossingToAPlainRouteNode_IsVacating()
    {
        if (N70csHeld(CrossingStop.NearHalf) is not { } held)
        {
            return;
        }

        Assert.True(held.Layout.Nodes.TryGetValue(held.FirstNodePastTheBar, out GroundNode? plain));
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, plain.Type);
        Attach(held.Crosser, new CrossingRunwayPhase(held.Phase.ApproachNodeId, plain.Id, "28R"), held.Runway);
        AircraftState taxiing = HeadOn(held.Crosser);

        bool vacating = RunwayOccupancy.IsVacating(held.Crosser, AircraftBody.Of(held.Crosser.AircraftType), held.Runway, held.Layout);
        List<string> lines = Resolve(held.Layout, held.Crosser, taxiing);

        Assert.True(vacating);
        Assert.Contains(lines, line => line.Contains("[Crossing] N70CS vacating runway → TAXI1 yields", StringComparison.Ordinal));
    }

    /// <summary>
    /// M1: the fallback crossing's target is the route's first node past the near bar, the runway's centreline node, whose side of
    /// the centreline is survey noise. N70CS held between the near bar and the pavement edge with its tail inside the hold line,
    /// on such a crossing, has not reached the pavement: it is not vacating 28R (nothing on the near side before the pavement).
    /// </summary>
    [Fact]
    public void N70csHeldBeforeThePavement_CrossingToTheCentrelineNode_IsNotVacating()
    {
        if (N70csHeld(CrossingStop.NearSideBeforeThePavement) is not { } held)
        {
            return;
        }

        Assert.False(RunwayOccupancy.IsOverStrictPavement(held.Crosser.Position, held.Runway), "N70CS should be stopped before the pavement");
        Assert.False(OnFarSide(held), "N70CS should be stopped on the near side");
        Assert.True(TailInsideTheLineOf(held, held.Phase.ApproachNodeId), "N70CS's tail should be inside the near hold line");
        Assert.True(held.Layout.Nodes.TryGetValue(held.FirstNodePastTheBar, out GroundNode? plain));
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, plain.Type);
        Attach(held.Crosser, new CrossingRunwayPhase(held.Phase.ApproachNodeId, plain.Id, "28R"), held.Runway);
        AircraftState taxiing = HeadOn(held.Crosser);

        bool vacating = RunwayOccupancy.IsVacating(held.Crosser, AircraftBody.Of(held.Crosser.AircraftType), held.Runway, held.Layout);
        List<string> lines = Resolve(held.Layout, held.Crosser, taxiing);

        Assert.False(vacating);
        Assert.DoesNotContain(lines, line => line.Contains("vacating runway", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains("N70CS(dir=", StringComparison.Ordinal) && line.Contains("TAXI1(dir=", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// B5, far side off the pavement: N70CS held with its main gear past 28R's far edge and its tail still inside the far hold
    /// line, still crossing, is vacating 28R by its side alone: head-on traffic gives way to it.
    /// </summary>
    [Fact]
    public void N70csHeldPastTheFarEdge_TailInsideTheFarLine_HeadOnTrafficYields()
    {
        if (N70csHeld(CrossingStop.FarSideOffThePavement) is not { } held)
        {
            return;
        }

        Assert.IsType<CrossingRunwayPhase>(held.Crosser.Phases?.CurrentPhase);
        Assert.False(RunwayOccupancy.IsOverStrictPavement(held.Crosser.Position, held.Runway), "N70CS's main gear should be past the far edge");
        Assert.True(OnFarSide(held), "N70CS should be stopped on the far side");
        Assert.True(TailInsideTheLineOf(held, held.Phase.TargetNodeId), "N70CS's tail should be inside the far hold line");
        AircraftState taxiing = HeadOn(held.Crosser);

        List<string> lines = Resolve(held.Layout, held.Crosser, taxiing);

        Assert.Equal(0.0, taxiing.Ground.SpeedLimit);
        Assert.Contains(lines, line => line.Contains("[Crossing] N70CS vacating runway → TAXI1 yields", StringComparison.Ordinal));
    }

    /// <summary>
    /// B7 / B12 mid-crossing: N70CS held on the near half of 28R, restored from its snapshot before its crossing rebuilds its slice,
    /// ranks the same — the head-on traffic gets the same limits as against the live one.
    /// </summary>
    [Fact]
    public void N70csHeldMidCrossing_SnapshotRoundTrip_GivesTheSameLimits()
    {
        if (N70csHeld(CrossingStop.NearHalf) is not { } held)
        {
            return;
        }

        AircraftState taxiing = HeadOn(held.Crosser);
        var restored = AircraftState.FromSnapshot(held.Crosser.ToSnapshot(), held.Layout);
        var restoredTraffic = AircraftState.FromSnapshot(taxiing.ToSnapshot(), held.Layout);

        Resolve(held.Layout, held.Crosser, taxiing);
        Resolve(held.Layout, restored, restoredTraffic);

        Assert.IsType<CrossingRunwayPhase>(restored.Phases?.CurrentPhase);
        Assert.Equal(taxiing.Ground.SpeedLimit, restoredTraffic.Ground.SpeedLimit);
        Assert.Equal(taxiing.Ground.AutoYieldTarget, restoredTraffic.Ground.AutoYieldTarget);
        Assert.Equal(held.Crosser.Ground.SpeedLimit, restored.Ground.SpeedLimit);
        Assert.Equal(0.0, restoredTraffic.Ground.SpeedLimit);
    }

    private const string N70csRecording = "TestData/n70cs-cross-stops-on-runway-recording.yaat-bug-report-bundle.zip";

    /// <summary>
    /// N70CS held mid-crossing, with its crossing phase and the first node of its route past the near bar it held short at.
    /// </summary>
    private sealed record HeldCrossing(
        AirportGroundLayout Layout,
        RunwayInfo Runway,
        AircraftState Crosser,
        CrossingRunwayPhase Phase,
        int FirstNodePastTheBar
    );

    /// <summary>Where in its crossing of 28R N70CS is given HOLD (<see cref="N70csHeld"/>).</summary>
    private enum CrossingStop
    {
        /// <summary>Main gear over the strict pavement, on the near half.</summary>
        NearHalf,

        /// <summary>Main gear over the strict pavement, on the far half.</summary>
        FarHalf,

        /// <summary>Main gear between the near bar and the pavement edge, tail inside the near hold line.</summary>
        NearSideBeforeThePavement,

        /// <summary>Main gear past the far pavement edge, still crossing.</summary>
        FarSideOffThePavement,
    }

    /// <summary>
    /// The N70CS recording (OAK) restored before its <c>TAXI &gt;J HS 28R</c>, which is replayed; held short of 28R, it is cleared
    /// across, and given HOLD on the first second it is at <paramref name="stop"/> (the near side is its approach bar's), then
    /// ticked to rest. Null when the recording or the layout is unavailable.
    /// </summary>
    private static HeldCrossing? N70csHeld(CrossingStop stop)
    {
        using RecordingArchive? archive = RecordingLoader.OpenArchive(N70csRecording);
        var groundData = RecordingGroundData.Evergreen("CROSS clearing past the far 28R bar");
        if ((archive is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        SessionRecording recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);
        TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(2410));
        engine.RestoreFromSnapshot(snapshot.State);
        engine.ReplayRange((int)snapshot.ElapsedSeconds, 2430, recording.Actions);
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        int nearBar = TickUntilHoldingShort(engine);
        TaxiRoute route = engine.FindAircraft("N70CS")!.Ground.AssignedTaxiRoute!;
        int firstPastTheBar = route.Segments.SkipWhile(s => s.ToNodeId != nearBar).Skip(1).First().ToNodeId;
        Assert.True(engine.SendCommand("N70CS", "CROSS").Success);

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            AircraftState crosser = engine.FindAircraft("N70CS")!;
            if (crosser.Phases?.CurrentPhase is not CrossingRunwayPhase crossing)
            {
                continue;
            }

            var candidate = new HeldCrossing(layout, runway, crosser, crossing, firstPastTheBar);
            if (IsAt(stop, candidate))
            {
                Assert.True(engine.SendCommand("N70CS", "HOLD").Success);
                TickToRest(engine, crosser);
                return candidate;
            }
        }

        Assert.Fail($"N70CS never reached {stop} in its crossing of 28R within 120 s");
        return null;
    }

    private static bool IsAt(CrossingStop stop, HeldCrossing held)
    {
        bool onPavement = RunwayOccupancy.IsOverStrictPavement(held.Crosser.Position, held.Runway);
        return stop switch
        {
            CrossingStop.NearHalf => onPavement && !OnFarSide(held),
            CrossingStop.FarHalf => onPavement && OnFarSide(held),
            CrossingStop.NearSideBeforeThePavement => !onPavement && !OnFarSide(held) && TailInsideTheLineOf(held, held.Phase.ApproachNodeId),
            CrossingStop.FarSideOffThePavement => !onPavement && OnFarSide(held),
            _ => throw new ArgumentOutOfRangeException(nameof(stop), stop, "unknown crossing stop"),
        };
    }

    /// <summary>
    /// Whether the crosser's tail lies more than <see cref="RunwayOccupancy.HoldLineFoulToleranceFt"/> nearer the centreline than
    /// bar <paramref name="barId"/>: measured apart from the predicate under test.
    /// </summary>
    private static bool TailInsideTheLineOf(HeldCrossing held, int barId)
    {
        AircraftState ac = held.Crosser;
        LatLon tail = AircraftBody.Of(ac.AircraftType).PointOf(AircraftBodyPart.Tail, ac.Position, ac.TrueHeading.Degrees);
        double barFt = RunwayHoldBars.CentrelineFt(held.Runway, held.Layout.Nodes[barId].Position);
        return RunwayHoldBars.CentrelineFt(held.Runway, tail) < (barFt - RunwayOccupancy.HoldLineFoulToleranceFt);
    }

    /// <summary>Ticks until N70CS holds short of 28R and returns the bar it holds at.</summary>
    private static int TickUntilHoldingShort(SimulationEngine engine)
    {
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            if (
                (engine.FindAircraft("N70CS")?.Phases?.CurrentPhase is HoldingShortPhase holding)
                && (holding.HoldShort.TargetName is { } target)
                && target.Contains("28R", StringComparison.Ordinal)
            )
            {
                return holding.HoldShort.NodeId;
            }
        }

        Assert.Fail("N70CS never held short of 28R within 120 s");
        return 0;
    }

    /// <summary>Ticks until <paramref name="ac"/> is held at rest (<see cref="GroundConflictDetector.IsParkedOrHeld(AircraftState)"/>).</summary>
    private static void TickToRest(SimulationEngine engine, AircraftState ac)
    {
        for (int t = 1; (t <= 30) && !GroundConflictDetector.IsParkedOrHeld(ac); t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(GroundConflictDetector.IsParkedOrHeld(ac), $"N70CS not held at rest 30 s after HOLD ({ac.GroundSpeed:F1} kt)");
    }

    /// <summary>Whether the crosser's main gear is across the centreline from its approach bar, the near bar it held at.</summary>
    private static bool OnFarSide(HeldCrossing held) =>
        RunwayHoldBars.IsRightOfCentreline(held.Runway, held.Crosser.Position)
        != RunwayHoldBars.IsRightOfCentreline(held.Runway, held.Layout.Nodes[held.Phase.ApproachNodeId].Position);

    /// <summary>A B738 160 ft ahead of <paramref name="crosser"/> along its heading, inside the trail band, taxiing back at it at 10 kt.</summary>
    private static AircraftState HeadOn(AircraftState crosser)
    {
        LatLon ahead = Project(crosser.Position, crosser.TrueHeading, 160.0);
        return Ground("TAXI1", "B738", ahead, crosser.TrueHeading.ToReciprocal(), 10.0, new TaxiingPhase(), runway: null);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// The SFO 28R high-speed-exit pose; null only when the SFO data is missing. With the data present a failed pose search fails
    /// the test rather than skipping it.
    /// </summary>
    private static ExitPose? ExitPose()
    {
        if (RunwayBarFixture.RunwayBar("KSFO", "28R") is null)
        {
            return null;
        }

        return Assert.IsType<ExitPose>(HighSpeedExitFixture.TailOverThePavementOnAHighSpeedExit("KSFO", "28R", "B77W"));
    }

    /// <summary>Runs one ground-conflict pass over the two aircraft on <paramref name="layout"/> and returns its diagnostic lines.</summary>
    private static List<string> Resolve(AirportGroundLayout layout, AircraftState a, AircraftState b)
    {
        var lines = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([a, b], layout, 0, lines.Add);
        return lines;
    }

    /// <summary>
    /// A B738 <paramref name="distanceFt"/> from <paramref name="target"/>, on the bearing <paramref name="offNoseDeg"/> off the
    /// target's heading, taxiing straight at it at 10 kt.
    /// </summary>
    private static AircraftState Converging(
        string callsign,
        AircraftState target,
        double offNoseDeg,
        double distanceFt,
        Phase phase,
        RunwayInfo? runway
    )
    {
        LatLon position = Project(target.Position, new TrueHeading((target.TrueHeading.Degrees + offNoseDeg + 360.0) % 360.0), distanceFt);
        var heading = new TrueHeading(GeoMath.BearingTo(position, target.Position));
        return Ground(callsign, "B738", position, heading, 10.0, phase, runway);
    }

    private static AircraftState Ground(
        string callsign,
        string type,
        LatLon position,
        TrueHeading heading,
        double speedKts,
        Phase phase,
        RunwayInfo? runway
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            IndicatedAirspeed = speedKts,
            IsOnGround = true,
        };
        Attach(ac, phase, runway);
        return ac;
    }

    private static void Attach(AircraftState ac, Phase phase, RunwayInfo? runway)
    {
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(phase);
        ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
    }

    private static LatLon Project(LatLon from, TrueHeading heading, double distanceFt) =>
        GeoMath.ProjectPoint(from, heading, distanceFt / GeoMath.FeetPerNm);
}

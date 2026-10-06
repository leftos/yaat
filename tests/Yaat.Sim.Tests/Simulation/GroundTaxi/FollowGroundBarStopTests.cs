using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A <c>FOLLOWG</c> follower stops at a runway holding position the way a taxiing aircraft does: it brakes at its
/// category's taxi brake rate, comes to rest with its nose at the hold line, and reports holding short at the taxiway it
/// is on.
///
/// <para>The FOLLOW montage's H1 clip (<c>tools/montage/follow/H1</c>): at KOAK, N52417 taxis from GA13 via F, C, B to
/// runway 28R, cleared across runway 33 at C; N738SP, parked at GA16, is told <c>FOLLOWG N52417</c> with no route of its
/// own, so it falls in behind the lead and, holding no crossing clearance, stops at the runway 33 holding position on C.
/// The recording showed it stop from 20 kt to 0 in one second, 115 ft short of the bar, and report "holding short runway
/// 15/33 at taxiway".</para>
///
/// <para>The hold line is the line through the bar node square to the taxiway edge leading into it (AIM 2-3-5a.1: no part
/// of the aircraft extends beyond the runway holding position marking); every stop here is measured from the nose, along
/// that edge.</para>
/// </summary>
public class FollowGroundBarStopTests(ITestOutputHelper output)
{
    /// <summary>Why the late-detected bar after a corner is skipped while followers drive a free line.</summary>
    private const string FreeLineFollowSkip =
        "Free-line FOLLOWG misses a hold bar ~150 ft to the side once the lead stops slowing it "
        + "(FindBarAhead looks ~120 ft ahead, 50 ft wide); YAAT-316 drives followers on the taxi graph";

    private const int HoldBudgetSeconds = 240;

    /// <summary>
    /// How far behind the hold line the follower's nose may come to rest: the 2 ft the taxi stop curve ends short of its
    /// stop, the 1 ft window the stop is taken in, and 1 ft for a sub-tick of creep. A taxiing aircraft holds within the
    /// same window, so the follower holding further back than this is not stopping where taxi would.
    /// </summary>
    private const double NoseStopToleranceFt = 4.0;

    /// <summary>How far (ft) from the bar the follower's approach side is read: well before any stop, never past the bar.</summary>
    private const double ApproachReadFt = 400.0;

    /// <summary>How far (ft) off the taxiway centreline the off-centreline follower rolls: a C172 stopped by the straight-line
    /// distance to the bar node puts its nose past the line from ~7.6 ft off.</summary>
    private const double CentrelineOffsetFt = 12.0;

    /// <summary>How long (s) the No. 2 follower is watched standing behind the lead at the bar.</summary>
    private const int NumberTwoSeconds = 20;

    private const string Scenario = """
        {
          "id": "01JH01G8F0L1W5T7R9N2K4X6QB",
          "name": "Montage H1 | Ground follow through a turn and a runway hold short",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH01N52417G8F0L1W5T7R9NB",
              "aircraftId": "N52417",
              "aircraftType": "C172",
              "transponderMode": "Standby",
              "startingConditions": { "type": "Parking", "parking": "GA13" },
              "onAltitudeProfile": false,
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KLVK",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 0,
                "route": "",
                "remarks": "/V/",
                "aircraftType": "C172"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            },
            {
              "id": "01JH01N738SPG8F0L1W5T7R9ND",
              "aircraftId": "N738SP",
              "aircraftType": "C172",
              "transponderMode": "Standby",
              "startingConditions": { "type": "Parking", "parking": "GA16" },
              "onAltitudeProfile": false,
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KLVK",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 0,
                "route": "",
                "remarks": "/V/",
                "aircraftType": "C172"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ],
          "atc": [
            {
              "id": "01G9CQW3XEE3RW84EBYKEBCPQD",
              "artccId": "ZOA",
              "facilityId": "OAK",
              "positionId": "01GEAMB98RKCPP9HCNPW5AVDA5",
              "autoConnect": true,
              "autoTrackAirportIds": []
            }
          ],
          "flightStripConfigurations": []
        }
        """;

    private static readonly (int Second, string Callsign, string Command)[] Script =
    [
        (1, KoakFollowClip.Lead, "CAINH"),
        (1, KoakFollowClip.Follower, "CAINH"),
        (2, KoakFollowClip.Lead, "TAXI F C B CROSS 33 RWY 28R"),
        (20, KoakFollowClip.Follower, $"FOLLOWG {KoakFollowClip.Lead}"),
    ];

    /// <summary>The follower never loses more speed in a second than the piston taxi brake rate allows.</summary>
    [Fact]
    public void Follower_BrakesIntoTheRunway33Bar_AtThePistonTaxiBrakeRate()
    {
        if (RunToHold(Scenario) is not { } run)
        {
            return;
        }

        (double lossKts, int atSecond) = KoakFollowClip.LargestSpeedLossPerSecond(run.Speeds);
        output.WriteLine($"largest one-second speed loss: {lossKts:F2} kt ending t={atSecond}");
        Assert.True(
            lossKts <= KoakFollowClip.MaxPistonSpeedLossPerSecondKts + 1e-6,
            $"N738SP lost {lossKts:F2} kt in the second ending t={atSecond}; a piston brakes at most "
                + $"{KoakFollowClip.MaxPistonSpeedLossPerSecondKts:F2} kt/s (taxi brake rate plus one sub-tick of snap)"
        );
    }

    /// <summary>The follower comes to rest with its nose at the runway 33 hold line on C, not short of it and not past it.</summary>
    [Fact]
    public void Follower_StopsWithItsNoseAtTheRunway33Bar()
    {
        if (RunToHold(Scenario) is not { } run)
        {
            return;
        }

        AssertNoseAtHoldLine(run.Follower, run.Bar, ApproachBearingDeg(run.Bar, run.Follower.Position));
    }

    /// <summary>The follower's hold-short report names the taxiway it is holding on.</summary>
    [Fact]
    public void Follower_HoldShortWarning_NamesTaxiwayC()
    {
        if (RunToHold(Scenario) is not { } run)
        {
            return;
        }

        string warning = Assert.Single(run.Warnings, w => w.Contains("holding short", StringComparison.Ordinal));
        output.WriteLine($"warning: {warning}");
        Assert.StartsWith($"{KoakFollowClip.Follower} holding short runway ", warning, StringComparison.Ordinal);
        Assert.EndsWith(" at C", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A follower told to follow late, its lead already across runway 33 and turned away, rounds the corner toward C at
    /// taxi speed and first sees the runway 33 bar inside its braking distance. It still never puts its nose past the hold
    /// line, and holds short there: braking at the routine rate when that makes the line, at its category's firm rate when
    /// only that does, and stopping at the line as a last resort when neither does.
    /// </summary>
    [Theory(Skip = FreeLineFollowSkip)]
    [InlineData(60)]
    public void LateDetectedBar_AfterCorner_NeverCrossesTheHoldLine(int followSecond)
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = NearSideRunway33BarOnC(layout, follower.Position);
        (int Second, string Callsign, string Command)[] script =
        [
            (1, KoakFollowClip.Lead, "CAINH"),
            (1, KoakFollowClip.Follower, "CAINH"),
            (2, KoakFollowClip.Lead, "TAXI F C B CROSS 33 RWY 28R"),
            (followSecond, KoakFollowClip.Follower, $"FOLLOWG {KoakFollowClip.Lead}"),
        ];

        double? approachDeg = null;
        double worstPastFt = double.NegativeInfinity;
        int worstAt = -1;
        var speeds = new List<(int Second, double SpeedKts)>();
        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            script,
            followSecond + HoldBudgetSeconds,
            second =>
            {
                double toBarFt = GeoMath.DistanceNm(follower.Position, bar.Position) * GeoMath.FeetPerNm;
                approachDeg ??= toBarFt <= ApproachReadFt ? ApproachBearingDeg(bar, follower.Position) : null;
                if (approachDeg is { } axis)
                {
                    speeds.Add((second, follower.GroundSpeed));
                    double pastFt = NosePastHoldLineFt(follower, bar, axis);
                    output.WriteLine(
                        $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} toBar={toBarFt:F0} nosePast={pastFt:F1} "
                            + $"lateral={LateralOffsetFt(follower, bar, axis):F1}"
                    );
                    if (pastFt > worstPastFt)
                    {
                        worstPastFt = pastFt;
                        worstAt = second;
                    }
                }

                return (follower.Phases?.CurrentPhase is HoldingShortPhase) || (worstPastFt > 200.0);
            }
        );

        (double lossKts, int lossAt) = KoakFollowClip.LargestSpeedLossPerSecond(speeds);
        output.WriteLine($"held at t={heldAt}; furthest the nose got past the hold line: {worstPastFt:F1} ft at t={worstAt}");
        output.WriteLine($"largest one-second speed loss on the approach: {lossKts:F2} kt ending t={lossAt}");
        Assert.True(worstPastFt <= 0.0, $"N738SP's nose went {worstPastFt:F1} ft past the runway 33 hold line (node {bar.Id}) at t={worstAt}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(bar.Id, hold.HoldShort.NodeId);
    }

    /// <summary>
    /// A follower told to follow far behind its lead — the lead already across runway 33 on C — taxis the taxiways to the lead's
    /// path rather than cutting across the field at the lead: every second of the follow its centre stays within
    /// <see cref="OnTaxiwayToleranceFt"/> of a taxi edge's centreline, and, holding no crossing of its own, it stops at the
    /// runway 33 holding position on C with its nose never past the hold line.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    public void Following_FarBehindOnKoakC_StaysOnTheTaxiwaysAndStopsAtBar518(int followSecond)
    {
        if (KoakFollowClip.Load(output, Scenario) is not { } engine)
        {
            return;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = NearSideRunway33BarOnC(layout, follower.Position);
        List<DirectionalEdge> edges = [.. layout.Nodes.Values.SelectMany(n => n.Edges).Distinct().Select(e => e.Directed(e.Nodes[0], e.Nodes[1]))];
        (int Second, string Callsign, string Command)[] script =
        [
            (1, KoakFollowClip.Lead, "CAINH"),
            (1, KoakFollowClip.Follower, "CAINH"),
            (2, KoakFollowClip.Lead, "TAXI F C B CROSS 33 RWY 28R"),
            (followSecond, KoakFollowClip.Follower, $"FOLLOWG {KoakFollowClip.Lead}"),
        ];

        double worstOffFt = 0.0;
        int worstOffAt = -1;
        double worstPastFt = double.NegativeInfinity;
        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            script,
            followSecond + HoldBudgetSeconds,
            second =>
            {
                if (second <= followSecond)
                {
                    return false;
                }

                double offFt = GroundConflictDetector.TrackClearanceFt(NearbyEdges(edges, follower.Position), follower.Position);
                double pastFt = NosePastHoldLineFt(follower, bar, ApproachBearingDeg(bar, follower.Position));
                worstPastFt = Math.Max(
                    worstPastFt,
                    GeoMath.DistanceNm(follower.Position, bar.Position) * GeoMath.FeetPerNm <= ApproachReadFt ? pastFt : double.NegativeInfinity
                );
                if (offFt > worstOffFt)
                {
                    worstOffFt = offFt;
                    worstOffAt = second;
                }

                output.WriteLine(
                    $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} offTaxiway={offFt:F1} nosePast={pastFt:F1}"
                );
                return follower.Phases?.CurrentPhase is HoldingShortPhase;
            }
        );

        output.WriteLine($"held at t={heldAt} (bar node {bar.Id}); farthest off a taxi edge {worstOffFt:F1} ft at t={worstOffAt}");
        Assert.True(worstOffFt <= OnTaxiwayToleranceFt, $"N738SP's centre was {worstOffFt:F1} ft off every taxi edge at t={worstOffAt}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(bar.Id, hold.HoldShort.NodeId);
        Assert.True(worstPastFt <= 0.0, $"N738SP's nose went {worstPastFt:F1} ft past the runway 33 hold line (node {bar.Id})");
    }

    /// <summary>
    /// A follower that occupied runway 33 in this follow and has exited it clear past bar 518 — 100 ft back on C, the bar behind
    /// its tail — no longer passes that runway's bars as an exit: turned back toward its lead across the runway, it stops at
    /// bar 518 with its nose at the hold line.
    /// </summary>
    [Fact]
    public void Following_ReapproachingARunwayItExited_StopsAtItsBar()
    {
        // The follower stands 100 ft back from bar 518 facing away from the runway, as one that has just exited 33 there.
        if (StartExitedRunway33(Scenario, followerBackFt: 100.0, followerHeadingOffDeg: 0.0) is not { } run)
        {
            return;
        }

        (SimulationEngine engine, AircraftState follower, GroundNode bar, double approachDeg) = run;
        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            [],
            HoldBudgetSeconds,
            second =>
            {
                output.WriteLine(
                    $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} hdg={follower.TrueHeading.Degrees:F0} "
                        + $"nosePast={NosePastHoldLineFt(follower, bar, approachDeg):F1}"
                );
                return (follower.Phases?.CurrentPhase is HoldingShortPhase) || (NosePastHoldLineFt(follower, bar, approachDeg) > 200.0);
            }
        );

        output.WriteLine($"held at t={heldAt}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(bar.Id, hold.HoldShort.NodeId);
        AssertNoseAtHoldLine(follower, bar, approachDeg);
    }

    /// <summary>
    /// A B744 that exited runway 33 onto C and stopped with its tail still inside bar 518's hold line, its follow route then
    /// leading back across 33 to its lead, stops at bar 518: a bar met heading toward the runway is a crossing, whichever runway
    /// the follower is exiting (AIM 4-3-21.b). It never reaches the runway pavement.
    /// </summary>
    [Fact]
    public void Following_LongAircraftTurnsBackBeforeClearingTheExitBar_StopsAtIt()
    {
        double halfLengthFt = AircraftLength.ResolveFt("B744") / 2.0;
        if (StartExitedRunway33(WithFollowerType("B744"), followerBackFt: halfLengthFt - 30.0, followerHeadingOffDeg: 0.0) is not { } run)
        {
            return;
        }

        (SimulationEngine engine, AircraftState follower, GroundNode bar, _) = run;
        AssertStopsAtBarOffRunway33(engine, follower, bar);
    }

    /// <summary>
    /// A follower that exited runway 33 and stands on C 350 ft back from bar 518 square to C, the bar abeam it rather than behind
    /// it, has cleared the runway by position — its tail farther from the centreline than any of 33's bars on its side — so the
    /// runway is dropped from the ones it is exiting, and turning down C back across 33 to its lead it stops at bar 518.
    /// </summary>
    [Fact]
    public void Following_ExitWhoseNearestBarIsBeside_DropsTheRunwayByPosition()
    {
        if (StartExitedRunway33(Scenario, followerBackFt: 350.0, followerHeadingOffDeg: 90.0) is not { } run)
        {
            return;
        }

        (SimulationEngine engine, AircraftState follower, GroundNode bar, _) = run;
        AssertStopsAtBarOffRunway33(engine, follower, bar);
    }

    /// <summary>
    /// Runs the clip until the follower holds short or reaches runway 33's pavement; asserts it held at <paramref name="bar"/>
    /// off the pavement.
    /// </summary>
    private void AssertStopsAtBarOffRunway33(SimulationEngine engine, AircraftState follower, GroundNode bar)
    {
        string airportId = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout).AirportId;
        RunwayInfo runway33 = RunwayOccupancy.AirportRunways(airportId).First(r => r.Id.Overlaps(RunwayIdentifier.Parse("33")));
        output.WriteLine($"runway {runway33.Id} at {airportId}");
        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            [],
            HoldBudgetSeconds,
            second =>
            {
                output.WriteLine(
                    $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} hdg={follower.TrueHeading.Degrees:F0} "
                        + $"toBar={GeoMath.DistanceNm(follower.Position, bar.Position) * GeoMath.FeetPerNm:F0}"
                );
                return (follower.Phases?.CurrentPhase is HoldingShortPhase) || RunwayOccupancy.IsOnPavement(follower, runway33);
            }
        );

        output.WriteLine($"ended at t={heldAt}");
        Assert.False(RunwayOccupancy.IsOnPavement(follower, runway33), $"{follower.Callsign} drove onto runway 33 with no crossing clearance");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(bar.Id, hold.HoldShort.NodeId);
    }

    private sealed record ExitedRun(SimulationEngine Engine, AircraftState Follower, GroundNode Bar, double ApproachDeg);

    /// <summary>
    /// The H1 clip on <paramref name="scenarioJson"/> with its lead held on C 150 ft past runway 33's far-side bar on C and its
    /// follower <paramref name="followerBackFt"/> back from bar 518 along C, heading <paramref name="followerHeadingOffDeg"/> off
    /// the bearing away from the runway, put straight into a follow of the lead that remembers runway 33 as the one it is exiting.
    /// </summary>
    private ExitedRun? StartExitedRunway33(string scenarioJson, double followerBackFt, double followerHeadingOffDeg)
    {
        if (KoakFollowClip.Load(output, scenarioJson) is not { } engine)
        {
            return null;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = NearSideRunway33BarOnC(layout, follower.Position);
        GroundNode farBar = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "33", "C").MaxBy(n => GeoMath.DistanceNm(bar.Position, n.Position))!;
        double approachDeg = ApproachBearingDeg(bar, follower.Position);
        double awayFromRunwayDeg = GeoMath.BearingTo(farBar.Position, bar.Position);

        lead.Position = OffsetFt(farBar.Position, (awayFromRunwayDeg + 180.0) % 360.0, 150.0);
        lead.TrueHeading = new TrueHeading((awayFromRunwayDeg + 180.0) % 360.0);
        lead.Phases = new PhaseList();
        lead.Phases.Add(new HoldingInPositionPhase());
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead, layout));
        follower.Position = OffsetFt(bar.Position, approachDeg, followerBackFt);
        follower.TrueHeading = new TrueHeading((approachDeg + followerHeadingOffDeg) % 360.0);
        var follow = FollowingPhase.FromSnapshot(
            new FollowingPhaseDto
            {
                Status = (int)PhaseStatus.Pending,
                ElapsedSeconds = 0,
                Requirements = [],
                TargetCallsign = KoakFollowClip.Lead,
                TimeSinceLastLog = 0,
                ExitingRunways = ["33"],
            },
            layout
        );
        follower.Phases = new PhaseList();
        follower.Phases.Add(follow);
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower, layout));
        return new ExitedRun(engine, follower, bar, approachDeg);
    }

    /// <summary>
    /// How far (ft) off a taxi edge's centreline a follower on the taxi graph may be: wide of a cut corner, never across the field.
    /// </summary>
    private const double OnTaxiwayToleranceFt = 25.0;

    /// <summary>The edges with an end within 5,000 ft of <paramref name="at"/>, the only ones that can be the nearest.</summary>
    private static List<DirectionalEdge> NearbyEdges(List<DirectionalEdge> edges, LatLon at) =>
        [
            .. edges.Where(e =>
                (GeoMath.DistanceNm(at, e.FromNode.Position) * GeoMath.FeetPerNm < 5000.0)
                || (GeoMath.DistanceNm(at, e.ToNode.Position) * GeoMath.FeetPerNm < 5000.0)
            ),
        ];

    /// <summary>
    /// A follower starting off the taxiway centreline, its stationary lead on C beyond the bar, rejoins the centreline on its
    /// follow route and stops with its nose at the hold line, not with its centre a half-length from the bar node and its nose past it.
    /// </summary>
    [Fact]
    public void OffCentrelineApproach_NoseStopsAtTheHoldLine()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = NearSideRunway33BarOnC(layout, follower.Position);
        double approachDeg = ApproachBearingDeg(bar, follower.Position);
        double towardRunwayDeg = (approachDeg + 180.0) % 360.0;
        double sideDeg = (approachDeg + 90.0) % 360.0;

        // Both CentrelineOffsetFt to the side of C: the follower 300 ft short of the bar, the lead stopped 150 ft beyond it, holding
        // in position (not parked, so the follow has a taxi path to join), so the follower starts its follow off the centreline.
        follower.Position = OffsetFt(OffsetFt(bar.Position, approachDeg, 300.0), sideDeg, CentrelineOffsetFt);
        follower.TrueHeading = new TrueHeading(towardRunwayDeg);
        lead.Position = OffsetFt(OffsetFt(bar.Position, towardRunwayDeg, 150.0), sideDeg, CentrelineOffsetFt);
        lead.TrueHeading = new TrueHeading(towardRunwayDeg);
        lead.Phases = new PhaseList();
        lead.Phases.Add(new HoldingInPositionPhase());
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead, layout));

        (int Second, string Callsign, string Command)[] script =
        [
            (1, KoakFollowClip.Follower, "CAINH"),
            (1, KoakFollowClip.Follower, $"FOLLOWG {KoakFollowClip.Lead}"),
        ];
        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            script,
            HoldBudgetSeconds,
            second =>
            {
                output.WriteLine(
                    $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} "
                        + $"nosePast={NosePastHoldLineFt(follower, bar, approachDeg):F1} lateral={LateralOffsetFt(follower, bar, approachDeg):F1}"
                );
                return (follower.Phases?.CurrentPhase is HoldingShortPhase) || (NosePastHoldLineFt(follower, bar, approachDeg) > 200.0);
            }
        );

        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        output.WriteLine($"held at t={heldAt}");
        Assert.Equal(bar.Id, hold.HoldShort.NodeId);
        Assert.True(LateralOffsetFt(follower, bar, approachDeg) < CentrelineOffsetFt / 2.0, "the follower held off the centreline it started beside");
        AssertNoseAtHoldLine(follower, bar, approachDeg);
    }

    /// <summary>
    /// A follower stopped behind a lead holding at the bar is No. 2, not holding short: it raises no hold-short report and
    /// stays in its follow. When the lead crosses, it moves up and takes the hold at the bar itself.
    /// </summary>
    [Fact]
    public void SecondInLine_ClosesUpAndHoldsAtTheBar()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        var warnings = new List<string>();
        engine.WarningEmitted += (callsign, text) =>
        {
            if ((callsign == KoakFollowClip.Follower) && text.Contains("holding short", StringComparison.Ordinal))
            {
                warnings.Add(text);
            }
        };

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = NearSideRunway33BarOnC(layout, follower.Position);
        (int Second, string Callsign, string Command)[] script =
        [
            (1, KoakFollowClip.Lead, "CAINH"),
            (1, KoakFollowClip.Follower, "CAINH"),
            (2, KoakFollowClip.Lead, "TAXI F C B RWY 28R"),
            (20, KoakFollowClip.Follower, $"FOLLOWG {KoakFollowClip.Lead}"),
        ];

        // Run until the lead holds at the bar and the follower has stood behind it for NumberTwoSeconds.
        int stoppedSince = -1;
        int numberTwoAt = KoakFollowClip.RunScript(
            engine,
            output,
            script,
            HoldBudgetSeconds,
            second =>
            {
                output.WriteLine(
                    $"t={second} lead={lead.Phases?.CurrentPhase?.Name} follower={follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1}"
                );
                bool numberTwo = (lead.Phases?.CurrentPhase is HoldingShortPhase) && (follower.GroundSpeed < 0.05) && (second > 20);
                stoppedSince = numberTwo ? (stoppedSince < 0 ? second : stoppedSince) : -1;
                return (stoppedSince > 0) && (second - stoppedSince >= NumberTwoSeconds);
            }
        );

        Assert.True(numberTwoAt > 0, $"N738SP never stood behind the lead at the bar within {HoldBudgetSeconds}s");
        Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.Empty(warnings);

        CommandResult cross = engine.SendCommand(KoakFollowClip.Lead, "CROSS 33");
        output.WriteLine($"{KoakFollowClip.Lead} CROSS 33 -> success={cross.Success} msg={cross.Message}");
        Assert.True(cross.Success, cross.Message);

        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            [],
            HoldBudgetSeconds,
            second =>
            {
                output.WriteLine($"+{second} follower={follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1}");
                return follower.Phases?.CurrentPhase is HoldingShortPhase;
            }
        );

        Assert.True(heldAt > 0, $"N738SP never moved up and held at the bar: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(bar.Id, hold.HoldShort.NodeId);
        AssertNoseAtHoldLine(follower, bar, ApproachBearingDeg(bar, follower.Position));
        Assert.Single(warnings);
    }

    /// <summary>
    /// A follower closing on the runway 33 bar on C at the piston taxi speed, its nose 110 ft from the hold line — inside its
    /// taxi-rate stopping distance, so only the max-effort rate makes the line — behind a lead stopped 150 ft past the bar,
    /// whose gap cap is the lower one at first. It still brakes at the rate the bar needs, not the taxi rate the gap cap asks
    /// for, and stops at the bar without the dead-stop backstop: it never loses more speed in a second than the max-effort rate
    /// allows, and holds with its nose at the line.
    /// </summary>
    [Fact]
    public void ClosingOnTheBarBehindASlowerLead_BrakesAtTheBarsRateAndStopsAtIt()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = NearSideRunway33BarOnC(layout, follower.Position);
        double approachDeg = ApproachBearingDeg(bar, follower.Position);
        double towardRunwayDeg = (approachDeg + 180.0) % 360.0;
        double halfLengthFt = AircraftLength.ResolveFt(follower.AircraftType) / 2.0;
        follower.Position = OffsetFt(bar.Position, approachDeg, 110.0 + halfLengthFt);
        follower.TrueHeading = new TrueHeading(towardRunwayDeg);
        lead.Position = OffsetFt(bar.Position, towardRunwayDeg, 150.0);
        lead.TrueHeading = new TrueHeading(towardRunwayDeg);
        lead.Phases = new PhaseList();
        lead.Phases.Add(new HoldingInPositionPhase());
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead, layout));
        Assert.True(engine.SendCommand(KoakFollowClip.Follower, "CAINH").Success);
        CommandResult follow = engine.SendCommand(KoakFollowClip.Follower, $"FOLLOWG {KoakFollowClip.Lead}");
        Assert.True(follow.Success, follow.Message);
        follower.IndicatedAirspeed = CategoryPerformance.TaxiSpeed(AircraftCategory.Piston);

        List<(int Second, double SpeedKts)> speeds = [(0, follower.GroundSpeed)];
        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            [],
            HoldBudgetSeconds,
            second =>
            {
                speeds.Add((second + 1, follower.GroundSpeed));
                output.WriteLine(
                    $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2} "
                        + $"nosePast={NosePastHoldLineFt(follower, bar, approachDeg):F1}"
                );
                return (follower.Phases?.CurrentPhase is HoldingShortPhase) || (NosePastHoldLineFt(follower, bar, approachDeg) > 200.0);
            }
        );

        output.WriteLine($"held at t={heldAt}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(bar.Id, hold.HoldShort.NodeId);
        (double lossKts, int atSecond) = KoakFollowClip.LargestSpeedLossPerSecond(speeds);
        double maxLossKts = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        Assert.True(
            lossKts <= maxLossKts + 1e-6,
            $"N738SP lost {lossKts:F2} kt in the second ending t={atSecond}, "
                + $"more than the {maxLossKts:F2} kt/s max-effort rate: the backstop stopped it"
        );
        AssertNoseAtHoldLine(follower, bar, approachDeg);
    }

    private static LatLon OffsetFt(LatLon from, double bearingDeg, double feet) =>
        GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), feet / GeoMath.FeetPerNm);

    /// <summary>
    /// A follow restored from a recording-JSON snapshot taken a few seconds before the follower turns from one taxiway onto
    /// the next carries the taxi edge it was on across the restore, and names the same taxiway as the run that was never
    /// interrupted, every second after.
    /// </summary>
    [Fact]
    public void Follower_TaxiwayAfterSnapshotRoundTrip_MatchesTheUninterruptedRun()
    {
        if (KoakFollowClip.Load(output, Scenario) is not { } reference)
        {
            return;
        }

        AircraftState referenceFollower = reference.FindAircraft(KoakFollowClip.Follower)!;
        var namesBySecond = new List<string?> { null };
        KoakFollowClip.RunScript(
            reference,
            output,
            Script,
            RoundTripCompareSeconds,
            _ =>
            {
                namesBySecond.Add(referenceFollower.Ground.CurrentTaxiway);
                return false;
            }
        );
        int turnAt = Enumerable
            .Range(FollowSecond + 2, RoundTripCompareSeconds - FollowSecond - 2)
            .FirstOrDefault(s => (namesBySecond[s] is not null) && (namesBySecond[s - 1] is not null) && (namesBySecond[s] != namesBySecond[s - 1]));
        Assert.True(turnAt > 0, "N738SP never turned from one named taxiway onto another");
        int snapshotAt = turnAt - RoundTripLeadSeconds;
        output.WriteLine($"turn {namesBySecond[turnAt - 1]} -> {namesBySecond[turnAt]} at t={turnAt}; snapshot at t={snapshotAt}");

        SimulationEngine original = Assert.IsType<SimulationEngine>(KoakFollowClip.Load(output, Scenario));
        Assert.Equal(snapshotAt, KoakFollowClip.RunScript(original, output, Script, snapshotAt, second => second == snapshotAt));
        // The restoring engine has the scenario loaded first, as a recording replay's has: the snapshot carries no scenario JSON.
        SimulationEngine restored = Assert.IsType<SimulationEngine>(KoakFollowClip.Load(output, Scenario));
        restored.RestoreFromSnapshot(RoundTrip(original.CaptureSnapshot()));
        Assert.NotNull(FollowTaxiEdge(original));
        Assert.Equal(FollowTaxiEdge(original), FollowTaxiEdge(restored));

        AircraftState restoredFollower = restored.FindAircraft(KoakFollowClip.Follower)!;
        for (int second = snapshotAt + 1; second < namesBySecond.Count; second++)
        {
            original.TickOneSecond();
            restored.TickOneSecond();
            Assert.True(
                restoredFollower.Ground.CurrentTaxiway == namesBySecond[second],
                $"t={second}: restored follower on '{restoredFollower.Ground.CurrentTaxiway}', the uninterrupted run on '{namesBySecond[second]}'"
            );
            Assert.Equal(FollowTaxiEdge(original), FollowTaxiEdge(restored));
        }
    }

    private const int FollowSecond = 20;
    private const int RoundTripCompareSeconds = 200;
    private const int RoundTripLeadSeconds = 3;

    private static string? FollowTaxiEdge(SimulationEngine engine) =>
        (engine.FindAircraft(KoakFollowClip.Follower)?.Phases?.CurrentPhase is FollowingPhase follow)
        && ((FollowingPhaseDto)follow.ToSnapshot() is { TaxiEdgeNodeA: { } nodeA, TaxiEdgeNodeB: { } nodeB })
            ? $"{nodeA}-{nodeB}"
            : null;

    /// <summary>The jet the jet-follower test puts in N738SP's place.</summary>
    private const string JetType = "B738";

    /// <summary>
    /// A B738 in N738SP's place follows the same lead and stops with its nose at the runway 33 hold line on C, braking no
    /// harder than the jet taxi brake rate (plus one sub-tick of snap): the routine stop, not the firm one.
    /// </summary>
    [Fact]
    public void JetFollower_StopsWithItsNoseAtTheRunway33Bar_AtTheJetTaxiBrakeRate()
    {
        if (RunToHold(WithFollowerType(JetType)) is not { } run)
        {
            return;
        }

        AssertNoseAtHoldLine(run.Follower, run.Bar, ApproachBearingDeg(run.Bar, run.Follower.Position));
        (double lossKts, int atSecond) = KoakFollowClip.LargestSpeedLossPerSecond(run.Speeds);
        double maxLossKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        output.WriteLine($"largest one-second speed loss: {lossKts:F2} kt ending t={atSecond}; the jet taxi rate allows {maxLossKts:F2}");
        Assert.True(
            lossKts <= maxLossKts + 1e-6,
            $"the {JetType} lost {lossKts:F2} kt in the second ending t={atSecond}, more than the taxi rate allows"
        );
    }

    /// <summary>
    /// Two B738s in trail on C, heading for the runway 33 bar: the lead taxis to runway 28R and holds at the bar; the
    /// follower, told to follow it, stops behind it with its nose at least the large-jet stop gap (150 ft) from the lead's
    /// tail, and its nose never reaches the lead's tail at any second of the run.
    /// </summary>
    [Fact]
    public void Following_GapIsNoseToTailAlongThePath()
    {
        string scenarioJson = WithAircraftType(WithAircraftType(Scenario, KoakFollowClip.Lead, JetType), KoakFollowClip.Follower, JetType);
        if (KoakFollowClip.Load(output, scenarioJson) is not { } engine)
        {
            return;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        double stopGapFt = FollowGap.StopGapFt(JetType, AircraftCategory.Jet, JetType, AircraftCategory.Jet);
        Assert.Equal(150.0, stopGapFt, 6);

        // Both on C's centreline short of the bar, the lead 250 ft and the follower 750 ft back: the pair start in trail,
        // the follower's nose well outside the close-follow band, so the run measures the follow and nothing else.
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = NearSideRunway33BarOnC(layout, follower.Position);
        double approachDeg = ApproachBearingDeg(bar, follower.Position);
        var towardRunway = new TrueHeading((approachDeg + 180.0) % 360.0);
        lead.Position = OffsetFt(bar.Position, approachDeg, 250.0);
        lead.TrueHeading = towardRunway;
        follower.Position = OffsetFt(bar.Position, approachDeg, 750.0);
        follower.TrueHeading = towardRunway;

        const int inTrailFollowSecond = 3;
        (int Second, string Callsign, string Command)[] script =
        [
            (1, KoakFollowClip.Lead, "CAINH"),
            (1, KoakFollowClip.Follower, "CAINH"),
            (2, KoakFollowClip.Lead, "TAXI C B RWY 28R"),
            (inTrailFollowSecond, KoakFollowClip.Follower, $"FOLLOWG {KoakFollowClip.Lead}"),
        ];

        double closestFt = double.PositiveInfinity;
        int closestAt = -1;
        int stoppedSince = -1;
        int settledAt = KoakFollowClip.RunScript(
            engine,
            output,
            script,
            HoldBudgetSeconds,
            second =>
            {
                double gapFt = SignedNoseToTailFt(follower, lead);
                if ((second > inTrailFollowSecond) && (gapFt < closestFt))
                {
                    closestFt = gapFt;
                    closestAt = second;
                }

                output.WriteLine(
                    $"t={second} lead={lead.Phases?.CurrentPhase?.Name} follower={follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} "
                        + $"noseToTail={gapFt:F1}"
                );
                bool settled = (lead.Phases?.CurrentPhase is HoldingShortPhase) && (follower.GroundSpeed < 0.05) && (second > inTrailFollowSecond);
                stoppedSince = settled ? (stoppedSince < 0 ? second : stoppedSince) : -1;
                return (stoppedSince > 0) && (second - stoppedSince >= NumberTwoSeconds);
            }
        );

        Assert.True(settledAt > 0, $"the follower never stood behind the lead at the bar within {HoldBudgetSeconds}s");
        double stoppedGapFt = SignedNoseToTailFt(follower, lead);
        output.WriteLine($"settled at t={settledAt}: nose {stoppedGapFt:F1} ft from the lead's tail; closest {closestFt:F1} ft at t={closestAt}");
        Assert.True(closestFt > 0.0, $"the follower's nose reached the lead's tail ({closestFt:F1} ft) at t={closestAt}");
        Assert.True(
            stoppedGapFt >= stopGapFt - NoseStopToleranceFt,
            $"the follower stopped with its nose {stoppedGapFt:F1} ft from the lead's tail; expected at least {stopGapFt - NoseStopToleranceFt:F1} ft"
        );
        Assert.True(
            stoppedGapFt <= stopGapFt + 10.0,
            $"the follower stopped with its nose {stoppedGapFt:F1} ft from the lead's tail; expected no more than {stopGapFt + 10.0:F1} ft"
        );
    }

    /// <summary>
    /// How far (ft) the follower's nose — centre plus half its length along its heading — is from the lead's tail — centre less
    /// half its length along its heading: positive while the tail is ahead of the nose along the line from the follower's
    /// centre to the lead's, negative once the nose is past it.
    /// </summary>
    private static double SignedNoseToTailFt(AircraftState follower, AircraftState lead)
    {
        LatLon nose = GeoMath.ProjectPoint(
            follower.Position,
            follower.TrueHeading,
            AircraftLength.ResolveFt(follower.AircraftType) / 2.0 / GeoMath.FeetPerNm
        );
        LatLon tail = GeoMath.ProjectPoint(
            lead.Position,
            new TrueHeading((lead.TrueHeading.Degrees + 180.0) % 360.0),
            AircraftLength.ResolveFt(lead.AircraftType) / 2.0 / GeoMath.FeetPerNm
        );
        double distFt = GeoMath.DistanceNm(nose, tail) * GeoMath.FeetPerNm;
        double towardLeadDeg = GeoMath.BearingTo(follower.Position, lead.Position);
        double offRad = GeoMath.SignedBearingDifference(towardLeadDeg, GeoMath.BearingTo(nose, tail)) * Math.PI / 180.0;
        return Math.Cos(offRad) >= 0.0 ? distFt : -distFt;
    }

    /// <summary>The H1 scenario with N738SP flying <paramref name="type"/> instead of a C172.</summary>
    private static string WithFollowerType(string type) => WithAircraftType(Scenario, KoakFollowClip.Follower, type);

    /// <summary><paramref name="scenarioJson"/> with <paramref name="callsign"/> flying <paramref name="type"/>.</summary>
    private static string WithAircraftType(string scenarioJson, string callsign, string type)
    {
        JsonNode scenario = Assert.IsAssignableFrom<JsonNode>(JsonNode.Parse(scenarioJson));
        JsonNode aircraft = Assert.Single(scenario["aircraft"]!.AsArray(), a => (string?)a?["aircraftId"] == callsign)!;
        aircraft["aircraftType"] = type;
        aircraft["flightplan"]!["aircraftType"] = type;
        return scenario.ToJsonString();
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    private sealed record HoldRun(AircraftState Follower, GroundNode Bar, List<(int Second, double SpeedKts)> Speeds, List<string> Warnings);

    /// <summary>
    /// Plays the clip on <paramref name="scenarioJson"/> until the follower holds short, recording its ground speed each
    /// second from the FOLLOWG on and the warnings it raises. Asserts the hold is the runway 33 bar on C.
    /// </summary>
    private HoldRun? RunToHold(string scenarioJson)
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, scenarioJson);
        if (engine is null)
        {
            return null;
        }

        var warnings = new List<string>();
        engine.WarningEmitted += (callsign, text) =>
        {
            if (callsign == KoakFollowClip.Follower)
            {
                warnings.Add(text);
            }
        };

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        var speeds = new List<(int Second, double SpeedKts)>();
        int heldAt = KoakFollowClip.RunScript(
            engine,
            output,
            Script,
            HoldBudgetSeconds,
            second =>
            {
                if (second > 20)
                {
                    speeds.Add((second, follower.GroundSpeed));
                    output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1}");
                }

                return follower.Phases?.CurrentPhase is HoldingShortPhase;
            }
        );

        Assert.True(heldAt > 0, $"N738SP never held short within {HoldBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        GroundNode bar = layout.Nodes[hold.HoldShort.NodeId];
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar.Type);
        Assert.True(bar.RunwayId?.Overlaps(RunwayIdentifier.Parse("33")) ?? false, $"held at node {bar.Id} for runway {bar.RunwayId}, not 33");
        return new HoldRun(follower, bar, speeds, warnings);
    }

    /// <summary>Asserts the follower's nose is at the hold line: not past it, and no further short of it than taxi would stop.</summary>
    private void AssertNoseAtHoldLine(AircraftState follower, GroundNode bar, double approachDeg)
    {
        double pastFt = NosePastHoldLineFt(follower, bar, approachDeg);
        output.WriteLine(
            $"held at bar node {bar.Id}: nose {pastFt:F1} ft past the hold line, centre {LateralOffsetFt(follower, bar, approachDeg):F1} ft off "
                + $"the centreline, heading {follower.TrueHeading.Degrees:F1}"
        );

        Assert.True(pastFt <= 0.0, $"{follower.Callsign}'s nose is {pastFt:F1} ft past the runway 33 hold line (node {bar.Id})");
        Assert.True(
            pastFt >= -NoseStopToleranceFt,
            $"{follower.Callsign} held with its nose {-pastFt:F1} ft short of the runway 33 hold line (node {bar.Id}); "
                + $"expected within {NoseStopToleranceFt:F0} ft"
        );
    }

    /// <summary>The runway 33 bar on C nearest <paramref name="from"/>: the one a follower coming from there meets first.</summary>
    private static GroundNode NearSideRunway33BarOnC(AirportGroundLayout layout, LatLon from)
    {
        List<GroundNode> bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "33", "C");
        Assert.NotEmpty(bars);
        return bars.MinBy(n => GeoMath.DistanceNm(from, n.Position))!;
    }

    /// <summary>
    /// The bearing from <paramref name="bar"/> back along the taxiway edge leading into it: of the bar node's
    /// non-runway edges, the one whose far end lies nearest in bearing to <paramref name="approachSide"/>.
    /// </summary>
    private static double ApproachBearingDeg(GroundNode bar, LatLon approachSide)
    {
        double towardSide = GeoMath.BearingTo(bar.Position, approachSide);
        return bar
            .Edges.Where(e => !e.IsRunwayCenterline)
            .Select(e => GeoMath.BearingTo(bar.Position, (e.Nodes[0].Id == bar.Id ? e.Nodes[1] : e.Nodes[0]).Position))
            .MinBy(bearing => Math.Abs(GeoMath.SignedBearingDifference(towardSide, bearing)));
    }

    /// <summary>
    /// How far (ft) the aircraft's nose — its centre plus half its length along its heading — is past the hold line through
    /// <paramref name="bar"/>, measured along the approach edge (<paramref name="approachDeg"/> points back up it). Negative
    /// when the nose is short of the line.
    /// </summary>
    private static double NosePastHoldLineFt(AircraftState aircraft, GroundNode bar, double approachDeg)
    {
        double halfLengthFt = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0;
        LatLon nose = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, halfLengthFt / GeoMath.FeetPerNm);
        (double alongFt, _) = AxisComponentsFt(nose, bar, approachDeg);
        return -alongFt;
    }

    /// <summary>How far (ft) the aircraft's centre is to the side of the approach edge's line through <paramref name="bar"/>.</summary>
    private static double LateralOffsetFt(AircraftState aircraft, GroundNode bar, double approachDeg)
    {
        (_, double acrossFt) = AxisComponentsFt(aircraft.Position, bar, approachDeg);
        return Math.Abs(acrossFt);
    }

    /// <summary><paramref name="point"/> relative to <paramref name="bar"/>: along the approach edge (positive back up it) and across it.</summary>
    private static (double AlongFt, double AcrossFt) AxisComponentsFt(LatLon point, GroundNode bar, double approachDeg)
    {
        double distFt = GeoMath.DistanceNm(bar.Position, point) * GeoMath.FeetPerNm;
        double offRad = GeoMath.SignedBearingDifference(approachDeg, GeoMath.BearingTo(bar.Position, point)) * Math.PI / 180.0;
        return (distFt * Math.Cos(offRad), distFt * Math.Sin(offRad));
    }
}

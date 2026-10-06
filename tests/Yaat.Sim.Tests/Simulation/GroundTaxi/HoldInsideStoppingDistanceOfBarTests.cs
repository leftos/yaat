using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A taxiing aircraft already inside its taxi-rate stopping distance of an uncleared runway bar's painted stop must still
/// stop the nose at or behind the runway holding position marking (AIM 2-3-5.a.1), whether it is told <c>HOLD</c>,
/// <c>GIVEWAY</c> or nothing at all: braking at the firm rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>)
/// when only that makes the line, and stopping dead at the line only when not even the firm rate does.
///
/// <para>The rig: at KOAK an aircraft taxis from GA16 via F, C, B to runway 28R, cleared across runway 33. Rolling at taxi
/// speed toward the 33 crossing on C, it is re-routed <c>TAXI C B RWY 28R</c> — no crossing clearance — which leaves the 33
/// bar uncleared and closer than its taxi-rate stopping distance, and is told <c>HOLD</c> or <c>GIVEWAY</c> (or nothing) on
/// the same sub-tick. The B738's half fuselage (64.8 ft) is longer than the 33 bar's last segment on C, so its painted stop
/// is set back onto an earlier segment; the C172's lies on the bar's own segment.</para>
/// </summary>
public class HoldInsideStoppingDistanceOfBarTests
{
    private const string Callsign = "N738SP";
    private const string Jet = "B738";
    private const string Piston = "C172";
    private const string Traffic = "N318GW";
    private const string FirstTaxi = "TAXI F C B CROSS 33 RWY 28R";
    private const string ReRoute = "TAXI C B RWY 28R";
    private const string CrossedRunway = "33";
    private const int ApproachBudgetSeconds = 400;
    private const int StopBudgetSeconds = 60;
    private const double StationaryKts = 0.01;

    /// <summary>How far back up the route (ft) the hold line's approach bearing is taken from.</summary>
    private const double ApproachBaseFt = 30.0;

    /// <summary>The least room (ft) beyond the firm-rate stopping distance for the firm-rate case: the firm rate must make it.</summary>
    private const double FirmMarginFt = 5.0;

    /// <summary>The least taxi-rate stopping distance (ft) the well-outside guard holds from: rolling, not starting off.</summary>
    private const double MinTaxiStopFt = 100.0;

    /// <summary>How much nearer the line (ft) than a taxi-rate stop from the HOLD the guard lets the nose come to rest.</summary>
    private const double GuardToleranceFt = 5.0;

    /// <summary>How far (ft) behind the held aircraft the GIVEWAY traffic is placed, on its track and heading.</summary>
    private const double TrafficBehindFt = 400.0;

    /// <summary>How much farther past the line (ft) than a firm-rate stop from the re-route the start-bar cases let the nose rest.</summary>
    private const double StartBarToleranceFt = 5.0;

    /// <summary>
    /// The most an aircraft not held may be rolling (kt) when the hold it takes at the bar stops it: the speed
    /// <see cref="TaxiingPhase"/> takes a set-back or start-node hold at, which its arrival at the bar's own stop stays under.
    /// </summary>
    private const double CrawlKts = 3.0;

    private const string Turboprop = "AT76";

    /// <summary>The plain TAXI to runway 28R that holds short of runway 33 on C, with no crossing clearance.</summary>
    private const string RoutineTaxi = "TAXI F C B RWY 28R";

    /// <summary>The margin (ft) <see cref="TaxiingPhase"/> takes a bar's last-resort stop inside, beyond one tick of travel.</summary>
    private const double TakeMarginFt = GroundNavigator.SetBackStopMarginFt + 1.0;

    /// <summary>How much farther short of the line (ft) than a last-tick stop the last-resort cases let the nose rest.</summary>
    private const double DeadStopToleranceFt = 2.0;

    private const int ReleaseSeconds = 15;

    /// <summary>How far (ft) the nose must move on after the crossing release.</summary>
    private const double MovedOnFt = 20.0;

    /// <summary>How far (ft) back toward the bar the nose may settle after the release (pose noise, not a reversal).</summary>
    private const double BackStepToleranceFt = 1.0;

    /// <summary>A turn (deg) off the rest heading this large after the release is the aircraft turning back.</summary>
    private const double ReversalDeg = 90.0;

    /// <summary>The taxiway the taxiway-bar cases hold short of: E crosses C east of runway 33 (KOAK junction node 343).</summary>
    private const string TaxiwayTarget = "E";

    /// <summary>The re-route, given east of runway 33 on C, that adds the hold short of taxiway E.</summary>
    private const string TaxiwayReRoute = "TAXI C B RWY 28R HS E";

    /// <summary>How close (ft) to the 33 bar's stop the giver comes before the crossing traffic on J is told to taxi.</summary>
    private const double TrafficTaxiTriggerFt = 600.0;

    /// <summary>How fast (kt) the crossing traffic must be rolling before the GIVEWAY: one to traffic standing still releases at once.</summary>
    private const double TrafficRollingKts = 5.0;

    /// <summary>The crossing traffic's taxi: J across C at the C/J junction (KOAK node 352), just past the runway 33 bars.</summary>
    private const string TrafficTaxiOnJ = "TAXI J RWY 28R";

    /// <summary>A C172 standing on taxiway J north-east of C (KOAK node 376), facing the C/J junction.</summary>
    private const string TrafficOnJ = """
          {
            "id": "01JH02N318GWG8F0L1W5T7R9NE",
            "aircraftId": "N318GW",
            "aircraftType": "C172",
            "transponderMode": "C",
            "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.730627, "lon": -122.216111 }, "heading": 254 },
            "onAltitudeProfile": false,
            "flightplan": {
              "rules": "VFR",
              "departure": "KOAK",
              "destination": "KSQL",
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
        """;

    private bool _trafficTaxiing;

    /// <summary>The taxi rig's starting conditions: parked at GA16.</summary>
    private const string ParkedAtGa16 = """{ "type": "Parking", "parking": "GA16" }""";

    /// <summary>Standing on taxiway C at KOAK spot I8R (node 4), heading down C toward the C/A junction.</summary>
    private const string OnCAtSpotI8R = """{ "type": "Coordinates", "coordinates": { "lat": 37.725692, "lon": -122.204250 }, "heading": 112 }""";

    /// <summary>KOAK spot I8R on taxiway C, west of the C/A junction.</summary>
    private const int SpotI8RNodeId = 4;

    /// <summary>The KOAK node where taxiway C meets taxiway A.</summary>
    private const int CAJunctionNodeId = 337;

    /// <summary>The taxi from spot I8R down C onto A.</summary>
    private const string TaxiCToA = "TAXI C A B RWY 28R";

    /// <summary>The re-route, given with the nose just past the C/A junction, whose HS A binds at the first A node past it.</summary>
    private const string TaxiCToAHoldShortA = "TAXI A B RWY 28R HS A";

    /// <summary>
    /// The expedited B738's taxi speed (kt), 1.3 times its 30 kt: its firm-rate stop (171 ft) is longer than the 150 ft the
    /// phase counts as near the route's start node.
    /// </summary>
    private const double ExpeditedJetKts = 39.0;

    /// <summary>The crawl (kt) the stationary case is re-routed at, so it stops at once with the centre still at the bar's node.</summary>
    private const double CreepKts = 2.0;

    /// <summary>
    /// Below this speed (kt) the rewind case swaps the running taxi phase for one restored from its snapshot: under the speed
    /// at which the B738's firm-rate stop no longer reaches back within the start node's radius, so a restored phase that
    /// lost its finding could not make it again, and above the crawl the hold is taken at.
    /// </summary>
    private const double RewindBelowKts = 6.0;

    /// <summary>The words of the warning for an aircraft whose nose is past an uncleared runway marking, braking or stationary.</summary>
    private const string NosePastWording = "ft past the hold line for";

    /// <summary>How that warning ends when the aircraft is standing still.</summary>
    private const string StationaryWording = ", stationary";

    /// <summary>The words of the warning for the last-resort stop short of an uncleared runway marking.</summary>
    private const string StoppedDeadShortWording = "stopped dead short of";

    private const string Scenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6QC",
          "name": "HOLD inside the stopping distance of an uncleared runway bar",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH02N738SPG8F0L1W5T7R9ND",
              "aircraftId": "N738SP",
              "aircraftType": "B738",
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
                "aircraftType": "B738"
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

    private readonly ITestOutputHelper _output;

    public HoldInsideStoppingDistanceOfBarTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>What the aircraft is told on the sub-tick it is re-routed.</summary>
    private enum Order
    {
        Hold,
        GiveWay,
        GiveWayToCrossingTraffic,
        Cross,
        None,
    }

    /// <summary>
    /// HOLD issued where the taxi rate overruns the 33 bar's painted stop but the jet firm rate makes it: the B738 brakes
    /// harder than the taxi rate, no harder than the firm rate, and comes to rest with its nose at or behind the hold line.
    /// </summary>
    [Fact]
    public void Hold_InsideTaxiStoppingDistance_FirmRateMakesTheLine_StopsTheNoseAtTheLine()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.Hold, InsideTaxiButFirmMakesIt));
        if (outcome is null)
        {
            return;
        }

        AssertNoseAtLine(outcome, Jet);
        AssertFirmBraking(outcome.LargestSubTickLossKts, Jet, AircraftCategory.Jet);
    }

    /// <summary>
    /// HOLD issued where not even the firm rate makes the 33 bar's painted stop: the last-resort stop keeps the nose at or
    /// behind the hold line, rather than the centre stopping on the bar node half a fuselage past it.
    /// </summary>
    [Fact]
    public void Hold_InsideFirmStoppingDistance_StopsTheNoseAtTheLineAsTheLastResort()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.Hold, InsideFirm));
        if (outcome is null)
        {
            return;
        }

        AssertNoseAtLine(outcome, Jet);
        AssertDeadStopOnlyOnTheLastTick(outcome, Jet, AircraftCategory.Jet);
    }

    /// <summary>
    /// Guard on the fix's scope: HOLD issued with the painted stop beyond twice the taxi-rate stopping distance stops the B738
    /// where it is, at the taxi rate, well short of the line.
    /// </summary>
    [Fact]
    public void Hold_WellOutsideTaxiStoppingDistance_StopsAtTheTaxiRateShortOfTheLine()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.Hold, WellOutside));
        if (outcome is null)
        {
            return;
        }

        double taxiStepKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) / SimulationEngine.PhysicsSubTickRate;
        double expectedShortFt = outcome.ToStopAtHoldFt - outcome.TaxiStopAtHoldFt;
        Assert.True(
            outcome.NosePastLineFt <= -expectedShortFt + GuardToleranceFt,
            $"the {Jet}'s nose stopped {-outcome.NosePastLineFt:F1} ft short of the line; "
                + $"braking at the taxi rate from the HOLD leaves {expectedShortFt:F1}"
        );
        Assert.True(
            outcome.LargestSubTickLossKts <= taxiStepKts + 1e-6,
            $"the {Jet} braked harder than the taxi rate ({outcome.LargestSubTickLossKts:F2} kt a sub-tick) for a stop it had room for"
        );
    }

    /// <summary>
    /// The same re-route with no HOLD, where the taxi rate overruns the 33 bar's set-back stop but the firm rate makes it: the
    /// B738 brakes at the firm rate and comes to rest with its nose at or behind the hold line.
    /// </summary>
    [Fact]
    public void NoHold_InsideTaxiStoppingDistance_FirmRateMakesTheLine_StopsTheNoseAtTheLine()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.None, InsideTaxiButFirmMakesIt));
        if (outcome is null)
        {
            return;
        }

        AssertNoseAtLine(outcome, Jet);
        AssertFirmBrakingToACrawl(outcome, Jet, AircraftCategory.Jet);
    }

    /// <summary>
    /// The same re-route with no HOLD, where not even the firm rate makes the 33 bar's set-back stop: the last-resort stop
    /// keeps the nose at or behind the hold line.
    /// </summary>
    [Fact]
    public void NoHold_InsideFirmStoppingDistance_StopsTheNoseAtTheLineAsTheLastResort()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.None, InsideFirm));
        if (outcome is null)
        {
            return;
        }

        AssertNoseAtLine(outcome, Jet);
        AssertDeadStopOnlyOnTheLastTick(outcome, Jet, AircraftCategory.Jet);
    }

    /// <summary>
    /// GIVEWAY to traffic behind on the same heading — which keeps the B738 held and gives it no give-way point, the traffic
    /// having no route — issued where not even the firm rate makes the 33 bar's set-back stop: the GIVEWAY's "never stops
    /// dead" yields to the bar, and the nose stops at or behind the hold line.
    /// </summary>
    [Fact]
    public void GiveWay_InsideFirmStoppingDistance_StopsTheNoseAtTheLine()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.GiveWay, InsideFirm));
        if (outcome is null)
        {
            return;
        }

        AssertNoseAtLine(outcome, Jet);
        AssertDeadStopOnlyOnTheLastTick(outcome, Jet, AircraftCategory.Jet);
    }

    /// <summary>
    /// GIVEWAY to traffic taxiing J across C, whose give-way point at the C/J junction lies past the uncleared 33 bar, issued
    /// where not even the firm rate makes the bar's set-back stop: the bar comes first, so the nose stops at or behind the hold
    /// line, and the dead stop the GIVEWAY otherwise never takes is taken there, on the last tick only.
    /// </summary>
    [Fact]
    public void GiveWay_WithGiveWayPointPastAnUnclearedBar_StopsAtTheBarLine()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.GiveWayToCrossingTraffic, InsideFirm));
        if (outcome is null)
        {
            return;
        }

        AssertNoseAtLine(outcome, Jet);
        AssertDeadStopOnlyOnTheLastTick(outcome, Jet, AircraftCategory.Jet);
    }

    /// <summary>
    /// East of runway 33 on C, the B738 is re-routed <c>TAXI C B RWY 28R HS E</c> where the taxi rate overruns taxiway E's
    /// set-back stop but the firm rate makes it: it brakes harder than the taxi rate, no harder than the firm rate, and comes
    /// to rest with its centre at or behind the painted stop — its nose at or behind the line — never stopped dead.
    /// </summary>
    [Fact]
    public void TaxiwayHoldShort_InsideFirmDistance_StopsFirmAtTheLine()
    {
        TaxiwayOutcome? outcome = RunTaxiwayBar(InsideTaxiButFirmMakesIt);
        if (outcome is null)
        {
            return;
        }

        Assert.True(
            outcome.CentrePastStopFt <= 0.0,
            $"the {Jet}'s centre came to rest {outcome.CentrePastStopFt:F1} ft past taxiway {TaxiwayTarget}'s painted stop: its nose is over the line"
        );
        AssertFirmBraking(outcome.LargestRollingLossKts, Jet, AircraftCategory.Jet);
        Assert.True(
            outcome.LargestStopFromKts <= CrawlKts,
            $"the {Jet} was stopped from {outcome.LargestStopFromKts:F2} kt, more than a {CrawlKts:F1} kt crawl"
        );
    }

    /// <summary>
    /// Re-routed at an expedited taxi with its centre already at or past the 33 bar's node, so the new route starts on the
    /// bar's node with the nose well over the line: the firm-rate stop is held on that bar however far it carries the
    /// aircraft from the node, so the B738 is never let go part-way as it slows — it comes to a stop, holds, and never
    /// re-accelerates.
    /// </summary>
    [Fact]
    public void PassedStartBar_Latched_StopDoesNotReleaseWhileSlowing()
    {
        Outcome? outcome = Run(
            new Case(Jet, AircraftCategory.Jet, Order.None, CentrePastBarNode) { Expedite = true, ReRouteAtKts = ExpeditedJetKts }
        );
        if (outcome is not null)
        {
            AssertStopsAndHoldsWithoutReaccelerating(outcome);
        }
    }

    /// <summary>
    /// The same expedited stop past the start bar, rewound part-way: the taxi phase's snapshot carries the bar it is stopping
    /// for, and the phase restored from it keeps stopping — it comes to a stop, holds, and never re-accelerates.
    /// </summary>
    [Fact]
    public void PassedStartBar_LatchSurvivesSnapshotRoundTrip()
    {
        Outcome? outcome = Run(
            new Case(Jet, AircraftCategory.Jet, Order.None, CentrePastBarNode)
            {
                Expedite = true,
                ReRouteAtKts = ExpeditedJetKts,
                RewindMidStop = true,
            }
        );
        if (outcome is not null)
        {
            Assert.True(outcome.Rewound, $"the {Jet} was never rewound below {RewindBelowKts:F0} kt while taxiing");
            AssertStopsAndHoldsWithoutReaccelerating(outcome);
        }
    }

    /// <summary>
    /// Re-routed with the B738's centre already past the 33 bar's set-back painted stop — the nose over the holding position
    /// marking — on a route that does not start on the bar's node: the line is lost, so the aircraft brakes at the firm rate
    /// to a crawl rather than stopping dead from taxi speed, takes the hold, and warns once that its nose is past the hold line.
    /// </summary>
    [Fact]
    public void LineAlreadyLost_RunwayBar_FirmStopThenHold()
    {
        var warnings = WarningLogCapture.Install();
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.None, LineLostOffTheBarNode) { LogInstalled = true });
        if (outcome is null)
        {
            return;
        }

        Assert.False(outcome.RouteStartsAtBar, $"{ReRoute} started the route on the runway {CrossedRunway} bar's node");
        AssertFirmBrakingToACrawl(outcome, Jet, AircraftCategory.Jet);
        Assert.IsType<HoldingShortPhase>(outcome.Aircraft.Phases?.CurrentPhase);
        Assert.Single(warnings.Warnings, w => w.Contains(NosePastWording, StringComparison.Ordinal));
        Assert.DoesNotContain(warnings.Warnings, w => w.Contains(StoppedDeadShortWording, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same lost line with a HOLD given on the re-route's sub-tick: the held B738 reaches the 33 bar's node still rolling
    /// and keeps braking at the firm rate, never stopped dead there from the speed it arrives with.
    /// </summary>
    [Fact]
    public void LineAlreadyLost_RunwayBar_Hold_FirmStopThenHold()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.Hold, LineLostOffTheBarNode));
        if (outcome is not null)
        {
            Assert.False(outcome.RouteStartsAtBar, $"{ReRoute} started the route on the runway {CrossedRunway} bar's node");
            AssertFirmBraking(outcome.LargestSubTickLossKts, Jet, AircraftCategory.Jet);
        }
    }

    /// <summary>
    /// Stopped by a creeping HOLD with its centre past the 33 bar's set-back painted stop, the B738 is re-routed again onto a
    /// route that does not start on the bar's node: it takes the hold where it stands and warns once that its nose is past the
    /// hold line, stationary.
    /// </summary>
    [Fact]
    public void StationaryPastNonStartRunwayBar_WarnsStoppedPastOnce()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.Hold, LineLostOffTheBarNode) { ReRouteAtKts = CreepKts });
        if (outcome is null)
        {
            return;
        }

        var warnings = WarningLogCapture.Install();
        CommandResult reRoute = outcome.Engine.SendCommand(Callsign, ReRoute);
        Assert.True(reRoute.Success, $"{ReRoute} was rejected: {reRoute.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(outcome.Aircraft.Ground.AssignedTaxiRoute);
        Assert.NotEqual(outcome.Line.Node.Id, route.Segments[0].FromNodeId);
        for (int second = 0; second < ReleaseSeconds; second++)
        {
            outcome.Engine.TickOneSecond();
        }

        string warning = Assert.Single(warnings.Warnings, w => w.Contains(NosePastWording, StringComparison.Ordinal));
        Assert.EndsWith(StationaryWording, warning, StringComparison.Ordinal);
        Assert.IsType<HoldingShortPhase>(outcome.Aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// Re-routed onto a route starting on the 33 bar's node and cleared across at once (<c>CROSS 33</c>), the B738 is told
    /// <c>HS 33</c> a second later, its nose still over the bar's marking: the re-armed start bar stops it at the firm rate and
    /// it holds short, the same whether the run goes on live or is rewound part-way through the stop.
    /// </summary>
    [Fact]
    public void StartBarReArmedByHs_HoldsTheSameLiveAndAfterRewind()
    {
        var reArmed = new Case(Jet, AircraftCategory.Jet, Order.Cross, static approach => approach.StartsAtBar) { ReArmStartBar = true };
        Outcome? live = Run(reArmed);
        if (live is null)
        {
            return;
        }

        Outcome rewound = Assert.IsType<Outcome>(Run(reArmed with { RewindMidStop = true }));
        Assert.True(rewound.Rewound, $"the {Jet} was never rewound below {RewindBelowKts:F0} kt while taxiing");
        Assert.True(live.RouteStartsAtBar, $"{ReRoute} did not start the route on the runway {CrossedRunway} bar's node");
        Assert.IsType<HoldingShortPhase>(live.Aircraft.Phases?.CurrentPhase);
        Assert.IsType<HoldingShortPhase>(rewound.Aircraft.Phases?.CurrentPhase);
    }

    /// <summary>The last-resort dead stop short of the 33 marking warns that it stopped dead short of the line, never past it.</summary>
    [Fact]
    public void DeadStopShortOfTheLine_WarnsStoppedDeadShort()
    {
        var warnings = WarningLogCapture.Install();
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.None, InsideFirm) { LogInstalled = true });
        if (outcome is null)
        {
            return;
        }

        Assert.Contains(warnings.Warnings, w => w.Contains(StoppedDeadShortWording, StringComparison.Ordinal));
        Assert.DoesNotContain(warnings.Warnings, w => w.Contains(NosePastWording, StringComparison.Ordinal));
    }

    /// <summary>
    /// Standing still with its centre at the 33 bar's node, where a HOLD given with a creeping re-route past the line stopped
    /// it, the B738 is re-routed again onto a route starting on the bar's node: it warns once that it stopped past the hold
    /// line.
    /// </summary>
    [Fact]
    public void StationaryPastStartBar_WarnsStoppedPastOnce()
    {
        Outcome? outcome = Run(new Case(Jet, AircraftCategory.Jet, Order.Hold, CentrePastBarNode) { ReRouteAtKts = CreepKts });
        if (outcome is null)
        {
            return;
        }

        var warnings = WarningLogCapture.Install();
        CommandResult reRoute = outcome.Engine.SendCommand(Callsign, ReRoute);
        Assert.True(reRoute.Success, $"{ReRoute} was rejected: {reRoute.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(outcome.Aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(outcome.Line.Node.Id, route.Segments[0].FromNodeId);
        for (int second = 0; second < ReleaseSeconds; second++)
        {
            outcome.Engine.TickOneSecond();
        }

        string warning = Assert.Single(warnings.Warnings, w => w.Contains(NosePastWording, StringComparison.Ordinal));
        Assert.EndsWith(StationaryWording, warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The B738 taxiing <c>TAXI C A B RWY 28R</c> from spot I8R is re-routed <c>TAXI A B RWY 28R HS A</c> with its nose just
    /// past the C/A junction: HS A binds at the next A node, whose set-back stop is already behind the aircraft. It brakes at
    /// the firm rate to a crawl and takes the hold there, never stopped dead from taxi speed.
    /// </summary>
    [Fact]
    public void TaxiwaySetBackHold_BrakesFirm_NoDeadStop()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            _output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(_output).EnableCategory("TaxiingPhase", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(Scenario.Replace(ParkedAtGa16, OnCAtSpotI8R, StringComparison.Ordinal), 1, MagneticDeclination.EvaluationDateUtc);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        Assert.True(engine.SendCommand(Callsign, TaxiCToA).Success, $"{TaxiCToA} was rejected");
        engine.TickOneSecond();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        GroundNode junction = layout.Nodes[CAJunctionNodeId];
        var line = new HoldLine(junction, GeoMath.BearingTo(junction.Position, layout.Nodes[SpotI8RNodeId].Position));

        bool reRouted = false;
        var losses = new LossTracker();
        void BeforeSubTick() => losses.Before(aircraft);

        void AfterSubTick()
        {
            if (reRouted)
            {
                losses.After(aircraft);
            }
        }

        // The re-route is given between whole seconds, as a controller's command lands.
        for (int second = 0; (second < ApproachBudgetSeconds) && !reRouted; second++)
        {
            if (line.NosePastFt(aircraft) >= 0.0)
            {
                double speedKts = aircraft.GroundSpeed;
                CommandResult reRoute = engine.SendCommand(Callsign, TaxiCToAHoldShortA);
                Assert.True(reRoute.Success, $"{TaxiCToAHoldShortA} was rejected: {reRoute.Message}");
                TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
                _output.WriteLine(
                    $"{TaxiCToAHoldShortA} at {speedKts:F1} kt, nose {line.NosePastFt(aircraft):F1} ft past node {CAJunctionNodeId}: "
                        + $"{route.ToSummary()}, holds short at "
                        + $"[{string.Join(", ", route.HoldShortPoints.Select(hs => $"{hs.NodeId}:{hs.TargetName}"))}]"
                );
                reRouted = true;
            }

            StepSecond(engine, BeforeSubTick, AfterSubTick);
        }

        Assert.True(reRouted, $"the {Jet} never reached the C/A junction on {TaxiCToA}");
        for (int second = 0; (second < StopBudgetSeconds) && !losses.IsAtRest; second++)
        {
            StepSecond(engine, BeforeSubTick, AfterSubTick);
        }

        _output.WriteLine(
            $"at rest: largest one-sub-tick loss {losses.LargestKts:F2} kt (from {losses.LargestFromKts:F2} kt), largest while rolling "
                + $"{losses.LargestRollingKts:F2} kt, largest stop from {losses.LargestStopFromKts:F2} kt, "
                + $"phase {aircraft.Phases?.CurrentPhase?.Name}"
        );
        Assert.True(losses.IsAtRest, $"the {Jet} never came to rest after {TaxiCToAHoldShortA}; gs={aircraft.GroundSpeed:F1}");
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        AssertFirmBraking(losses.LargestRollingKts, Jet, AircraftCategory.Jet);
        Assert.True(
            losses.LargestStopFromKts <= CrawlKts,
            $"the {Jet} was stopped from {losses.LargestStopFromKts:F2} kt, more than a {CrawlKts:F1} kt crawl"
        );
    }

    /// <summary>
    /// After a re-route onto a route starting on the bar's node: the route did start there, the aircraft never gained speed
    /// in any sub-tick, and it came to rest holding short.
    /// </summary>
    private static void AssertStopsAndHoldsWithoutReaccelerating(Outcome outcome)
    {
        Assert.True(outcome.RouteStartsAtBar, $"{ReRoute} did not start the route on the runway {CrossedRunway} bar's node");
        Assert.True(outcome.LargestGainKts <= 1e-6, $"the {Jet} re-accelerated by {outcome.LargestGainKts:F2} kt in one sub-tick after the re-route");
        Assert.IsType<HoldingShortPhase>(outcome.Aircraft.Phases?.CurrentPhase);
    }

    /// <summary>The route would start on the bar's node with the aircraft's centre at or past that node.</summary>
    private static bool CentrePastBarNode(Approach approach) => approach.StartsAtBar && (approach.CentrePastNodeFt >= 0.0);

    /// <summary>The centre is past the bar's painted stop, and a route given now would not start on the bar's node.</summary>
    private static bool LineLostOffTheBarNode(Approach approach) => (approach.ToStopFt < 0.0) && !approach.StartsAtBar;

    /// <summary>
    /// A C172 re-routed with no HOLD inside its taxi-rate stopping distance of the 33 bar's painted stop, which lies on the
    /// bar's own segment: it brakes no harder than the piston firm rate rather than stopping dead from taxi speed on arrival,
    /// and comes to rest with its nose at or behind the hold line.
    /// </summary>
    [Fact]
    public void NoHold_Piston_InsideStoppingDistance_NeverStopsDeadFromTaxiSpeed()
    {
        Outcome? outcome = Run(new Case(Piston, AircraftCategory.Piston, Order.None, InsideTaxiButFirmMakesIt));
        if (outcome is null)
        {
            return;
        }

        AssertNoseAtLine(outcome, Piston);
        AssertFirmBrakingToACrawl(outcome, Piston, AircraftCategory.Piston);
    }

    /// <summary>
    /// Re-routed close enough to the 33 bar that the new route starts on the bar's node — which a TAXI only does once the nose
    /// is already over the hold line — and told HOLD on the same sub-tick: the line is lost, so the aircraft stops as soon as
    /// the firm rate lets it, short of runway 33's pavement, rather than at the taxi rate onto the runway.
    /// </summary>
    [Theory]
    [InlineData(Piston)]
    [InlineData(Jet)]
    public void Hold_RouteStartingPastUnclearedBar_StopsAtTheFirmRate(string type)
    {
        Outcome? outcome = RunPastStartBar(type, Order.Hold, expedite: false);
        if (outcome is not null)
        {
            AssertOffTheRunway(outcome, type);
        }
    }

    /// <summary>The same re-route onto a route starting on the 33 bar's node, with no HOLD: the firm-rate stop, then the hold short.</summary>
    [Theory]
    [InlineData(Piston)]
    [InlineData(Jet)]
    public void NoHold_RouteStartingPastUnclearedBar_StopsAtTheFirmRate(string type)
    {
        Outcome? outcome = RunPastStartBar(type, Order.None, expedite: false);
        if (outcome is not null)
        {
            AssertOffTheRunway(outcome, type);
            Assert.IsType<HoldingShortPhase>(outcome.Aircraft.Phases?.CurrentPhase);
        }
    }

    /// <summary>
    /// The start-bar re-route at an expedited taxi (the B738 at 39 kt, a firm-rate stop longer than the start-node radius):
    /// the stop is never let go part-way, and the aircraft holds where the firm rate leaves it.
    /// </summary>
    [Fact]
    public void NoHold_RouteStartingPastUnclearedBar_Expedited_StillStopsAndHolds()
    {
        Outcome? outcome = RunPastStartBar(Jet, Order.None, expedite: true);
        if (outcome is not null)
        {
            Assert.IsType<HoldingShortPhase>(outcome.Aircraft.Phases?.CurrentPhase);
        }
    }

    /// <summary>
    /// Released with <c>CROSS 33</c> from the hold taken past the start bar, the aircraft carries on forward across runway 33
    /// on its own heading: the crossing never aims it back at the bar node behind it.
    /// </summary>
    [Theory]
    [InlineData(Piston)]
    [InlineData(Jet)]
    public void RouteStartingPastUnclearedBar_CrossRelease_CarriesOnForward(string type)
    {
        Outcome? outcome = RunPastStartBar(type, Order.None, expedite: false);
        if (outcome is null)
        {
            return;
        }

        AircraftState aircraft = outcome.Aircraft;
        double restHeading = aircraft.TrueHeading.Degrees;
        Assert.True(outcome.Engine.SendCommand(Callsign, $"CROSS {CrossedRunway}").Success);
        double leastNosePastFt = outcome.NosePastLineFt;
        double mostTurnDeg = 0.0;
        for (int second = 0; second < ReleaseSeconds; second++)
        {
            outcome.Engine.TickOneSecond();
            leastNosePastFt = Math.Min(leastNosePastFt, outcome.Line.NosePastFt(aircraft));
            mostTurnDeg = Math.Max(mostTurnDeg, GeoMath.AbsBearingDifference(restHeading, aircraft.TrueHeading.Degrees));
        }

        double nowPastFt = outcome.Line.NosePastFt(aircraft);
        _output.WriteLine(
            $"after CROSS {CrossedRunway}: nose {nowPastFt:F1} ft past the line (least {leastNosePastFt:F1}), most turn off the rest heading "
                + $"{mostTurnDeg:F0} deg, phase {aircraft.Phases?.CurrentPhase?.Name}"
        );
        Assert.True(nowPastFt > outcome.NosePastLineFt + MovedOnFt, $"the {type} did not move on after CROSS {CrossedRunway}");
        Assert.True(leastNosePastFt >= outcome.NosePastLineFt - BackStepToleranceFt, $"the {type} moved back toward the bar after CROSS");
        Assert.True(mostTurnDeg < ReversalDeg, $"the {type} turned {mostTurnDeg:F0} deg off its heading after CROSS {CrossedRunway}");
    }

    /// <summary>
    /// A plain TAXI to the uncleared 33 bar, with no re-route and no HOLD: the navigator's routine approach brakes at the
    /// taxi rate all the way to the bar's stop, and the bar's braking never escalates — no firm rate, no dead stop.
    /// </summary>
    [Theory]
    [InlineData(Turboprop, AircraftCategory.Turboprop)]
    [InlineData(Jet, AircraftCategory.Jet)]
    [InlineData(Piston, AircraftCategory.Piston)]
    public void RoutineTaxiToUnclearedBar_BrakesAtTheTaxiRate(string type, AircraftCategory category)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            _output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(Scenario.Replace(Jet, type, StringComparison.Ordinal), 1, MagneticDeclination.EvaluationDateUtc);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        Assert.True(engine.SendCommand(Callsign, RoutineTaxi).Success);
        engine.TickOneSecond();
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        (int barSegment, HoldShortPoint bar) = BarAhead(route);
        Assert.False(bar.IsCleared, $"{RoutineTaxi} cleared the runway {CrossedRunway} bar");
        var line = HoldLine.Through(route, barSegment);

        var losses = new LossTracker();
        for (int second = 0; (second < ApproachBudgetSeconds) && !((aircraft.Phases?.CurrentPhase is HoldingShortPhase) && losses.IsAtRest); second++)
        {
            StepSecond(engine, () => losses.Before(aircraft), () => losses.After(aircraft));
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        double taxiStepKts = CategoryPerformance.TaxiDecelRate(category) / SimulationEngine.PhysicsSubTickRate;
        _output.WriteLine(
            $"{type} routine stop: nose {line.NosePastFt(aircraft):F1} ft past the line, largest one-sub-tick loss {losses.LargestKts:F2} kt "
                + $"(from {losses.LargestFromKts:F2} kt), largest while rolling {losses.LargestRollingKts:F2} kt, largest stop from "
                + $"{losses.LargestStopFromKts:F2} kt, taxi step {taxiStepKts:F2} kt"
        );
        Assert.True(line.NosePastFt(aircraft) <= 0.0, $"the {type}'s nose came to rest over the line");
        Assert.True(
            losses.LargestRollingKts <= taxiStepKts + 1e-6,
            $"the {type} lost {losses.LargestRollingKts:F2} kt in one sub-tick while rolling on a routine stop"
        );
    }

    /// <summary>
    /// The per-sub-tick speed changes of one aircraft: the largest loss and the speed it came from, the largest loss while
    /// still rolling, the largest speed any stop came from, and the largest gain.
    /// </summary>
    private sealed class LossTracker
    {
        private double _before;
        private int _stillSubTicks;

        public double LargestKts { get; private set; }

        public double LargestFromKts { get; private set; }

        public double LargestRollingKts { get; private set; }

        public double LargestStopFromKts { get; private set; }

        public double LargestGainKts { get; private set; }

        /// <summary>Where the largest loss happened, as the run that owns the tracker describes it.</summary>
        public string LargestAt { get; set; } = "never";

        public bool IsAtRest => _stillSubTicks >= (2 * SimulationEngine.PhysicsSubTickRate);

        public void Before(AircraftState aircraft) => _before = aircraft.GroundSpeed;

        public void After(AircraftState aircraft)
        {
            double lossKts = _before - aircraft.GroundSpeed;
            if (lossKts > LargestKts)
            {
                LargestKts = lossKts;
                LargestFromKts = _before;
            }

            LargestGainKts = Math.Max(LargestGainKts, -lossKts);
            if (aircraft.GroundSpeed >= StationaryKts)
            {
                LargestRollingKts = Math.Max(LargestRollingKts, lossKts);
            }
            else if (_before >= StationaryKts)
            {
                LargestStopFromKts = Math.Max(LargestStopFromKts, _before);
            }

            _stillSubTicks = aircraft.GroundSpeed < StationaryKts ? _stillSubTicks + 1 : 0;
        }
    }

    /// <summary>
    /// Runs the re-route that starts the route on the 33 bar's node, the nose already over the line, and checks the firm-rate
    /// stop: harder than the taxi rate, no harder than the firm rate, and no farther past the line than a firm-rate stop from
    /// where the re-route found it. Null when the KOAK layout is unavailable.
    /// </summary>
    private Outcome? RunPastStartBar(string type, Order order, bool expedite)
    {
        AircraftCategory category = AircraftCategorization.Categorize(type);
        Outcome? outcome = Run(new Case(type, category, order, static approach => approach.StartsAtBar) { Expedite = expedite });
        if (outcome is null)
        {
            return null;
        }

        Assert.True(outcome.RouteStartsAtBar, $"{ReRoute} did not start the route on the runway {CrossedRunway} bar's node");
        AssertFirmBraking(outcome.LargestSubTickLossKts, type, category);
        double limitFt = outcome.NosePastAtOrderFt + outcome.FirmStopAtOrderFt + StartBarToleranceFt;
        Assert.True(
            outcome.NosePastLineFt <= limitFt,
            $"the {type}'s nose came to rest {outcome.NosePastLineFt:F1} ft past the line; "
                + $"a firm-rate stop from the re-route leaves it {limitFt - StartBarToleranceFt:F1}"
        );
        return outcome;
    }

    private static void AssertOffTheRunway(Outcome outcome, string type)
    {
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", CrossedRunway));
        Assert.False(
            RunwayOccupancy.IsWithinPavement(outcome.RestNose, runway),
            $"the {type}'s nose came to rest on runway {CrossedRunway}'s pavement"
        );
    }

    /// <summary>
    /// The last-resort stop is taken on the last tick only: braking stays within the firm rate while the aircraft rolls, and
    /// the dead stop leaves the nose no farther short of the line than one tick of firm-rate travel from the speed it was
    /// stopped from, plus the take margin.
    /// </summary>
    private static void AssertDeadStopOnlyOnTheLastTick(Outcome outcome, string type, AircraftCategory category)
    {
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        double dt = 1.0 / SimulationEngine.PhysicsSubTickRate;
        Assert.True(
            outcome.LargestBrakingLossKts <= (firmRate * dt) + 1e-6,
            $"the {type} lost {outcome.LargestBrakingLossKts:F2} kt in one sub-tick while still rolling, more than the firm rate allows"
        );
        double fromKts = outcome.LargestLossFromKts;
        double oneTickFt = ((fromKts * dt) - (0.5 * firmRate * dt * dt)) * (GeoMath.FeetPerNm / 3600.0);
        double furthestBackFt = oneTickFt + TakeMarginFt + DeadStopToleranceFt;
        Assert.True(
            outcome.NosePastLineFt >= -furthestBackFt,
            $"the {type}'s nose came to rest {-outcome.NosePastLineFt:F1} ft short of the line, more than a last-tick stop from "
                + $"{fromKts:F1} kt leaves ({furthestBackFt:F1})"
        );
    }

    private static void AssertNoseAtLine(Outcome outcome, string type) =>
        Assert.True(
            outcome.NosePastLineFt <= 0.0,
            $"the {type}'s nose came to rest {outcome.NosePastLineFt:F1} ft over the runway {CrossedRunway} holding position marking"
        );

    private static void AssertFirmBraking(double lossKts, string type, AircraftCategory category)
    {
        double taxiStepKts = CategoryPerformance.TaxiDecelRate(category) / SimulationEngine.PhysicsSubTickRate;
        double firmStepKts = CategoryPerformance.ExpediteExitDecelRate(category) / SimulationEngine.PhysicsSubTickRate;
        Assert.True(lossKts > taxiStepKts + 1e-6, $"the {type} braked no harder than the taxi rate ({lossKts:F2} kt a sub-tick)");
        Assert.True(
            lossKts <= firmStepKts + 1e-6,
            $"the {type} lost {lossKts:F2} kt in one sub-tick, more than the firm rate allows ({firmStepKts:F2})"
        );
    }

    /// <summary>
    /// An aircraft not held brakes no harder than the firm rate while it rolls, and the hold it then takes at the bar stops it
    /// from no more than a crawl — never dead from taxi speed.
    /// </summary>
    private static void AssertFirmBrakingToACrawl(Outcome outcome, string type, AircraftCategory category)
    {
        AssertFirmBraking(outcome.LargestBrakingLossKts, type, category);
        Assert.True(
            outcome.LargestStopFromKts <= CrawlKts,
            $"the {type} was stopped from {outcome.LargestStopFromKts:F2} kt, more than a {CrawlKts:F1} kt crawl"
        );
    }

    /// <summary>Past the midpoint between the firm- and taxi-rate stopping distances, with the firm rate still making the stop.</summary>
    private static bool InsideTaxiButFirmMakesIt(Approach approach) =>
        (approach.ToStopFt <= (approach.TaxiStopFt + approach.FirmStopFt) / 2.0) && (approach.ToStopFt >= approach.FirmStopFt + FirmMarginFt);

    /// <summary>Within half the firm-rate stopping distance of the stop: the taxi rate carries the centre to the bar node.</summary>
    private static bool InsideFirm(Approach approach) => (approach.ToStopFt > 0.0) && (approach.ToStopFt <= approach.FirmStopFt / 2.0);

    /// <summary>Rolling at taxi speed with the stop between two and two and a half taxi-rate stopping distances ahead.</summary>
    private static bool WellOutside(Approach approach) =>
        (approach.TaxiStopFt >= MinTaxiStopFt)
        && (approach.ToStopFt >= 2.0 * approach.TaxiStopFt)
        && (approach.ToStopFt <= 2.5 * approach.TaxiStopFt);

    /// <summary>
    /// Where the aircraft stands on one sub-tick of its approach: along-route distance to the bar's painted stop, its taxi-
    /// and firm-rate stopping distances, whether a TAXI issued now would start the route on the bar's node (the taxi graph's
    /// nearest node to the aircraft on its heading is the bar's), and how far (ft) its centre is past the hold line through
    /// that node, negative when short.
    /// </summary>
    private sealed record Approach(double ToStopFt, double TaxiStopFt, double FirmStopFt, bool StartsAtBar, double CentrePastNodeFt);

    private sealed record Case(string Type, AircraftCategory Category, Order Order, Func<Approach, bool> When)
    {
        /// <summary>Whether the aircraft taxis expedited (<see cref="AircraftGroundOps.IsExpeditingTaxi"/>) toward the bar.</summary>
        public bool Expedite { get; init; }

        /// <summary>
        /// Whether to swap the running taxi phase, once below <see cref="RewindBelowKts"/> after the re-route, for one restored
        /// from its own snapshot.
        /// </summary>
        public bool RewindMidStop { get; init; }

        /// <summary>Whether the test installed its own SimLog factory, which the run then keeps.</summary>
        public bool LogInstalled { get; init; }

        /// <summary>The speed (kt) the aircraft is set to on the sub-tick it is re-routed, or null to keep its own.</summary>
        public double? ReRouteAtKts { get; init; }

        /// <summary>Whether to re-arm the runway 33 bar with <c>HS 33</c> one second after the re-route.</summary>
        public bool ReArmStartBar { get; init; }
    }

    private sealed record Outcome(
        SimulationEngine Engine,
        AircraftState Aircraft,
        HoldLine Line,
        double NosePastLineFt,
        double LargestSubTickLossKts,
        double LargestLossFromKts,
        double LargestBrakingLossKts,
        double LargestStopFromKts,
        double LargestGainKts,
        double ToStopAtHoldFt,
        double TaxiStopAtHoldFt,
        bool RouteStartsAtBar,
        double NosePastAtOrderFt,
        double FirmStopAtOrderFt,
        bool Rewound,
        LatLon RestNose
    );

    /// <summary>
    /// Taxis the aircraft toward the 33 crossing and, on the first sub-tick <paramref name="c"/>'s condition holds, re-routes
    /// it without the crossing and gives it the case's order; then runs it to rest. Null when the KOAK layout is unavailable.
    /// </summary>
    private Outcome? Run(Case c)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            _output.WriteLine("SKIP: KOAK layout unavailable");
            return null;
        }

        if (!c.LogInstalled)
        {
            SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        }

        var engine = new SimulationEngine(groundData);
        string scenario =
            c.Order == Order.GiveWayToCrossingTraffic
                ? Scenario.Replace("\"aircraft\": [", "\"aircraft\": [" + TrafficOnJ, StringComparison.Ordinal)
                : Scenario;
        engine.LoadScenario(scenario.Replace(Jet, c.Type, StringComparison.Ordinal), 1, MagneticDeclination.EvaluationDateUtc);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        Assert.True(engine.SendCommand(Callsign, FirstTaxi).Success);
        aircraft.Ground.IsExpeditingTaxi = c.Expedite;
        engine.TickOneSecond();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        TaxiRoute firstRoute = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        (int barSegment, HoldShortPoint clearedBar) = BarAhead(firstRoute);
        Assert.True(clearedBar.IsCleared, $"{FirstTaxi} left the runway {CrossedRunway} bar uncleared");
        var approachRig = new ApproachRig(layout, firstRoute, barSegment, clearedBar, HoldLine.Through(firstRoute, barSegment));

        ReRouted? reRouted = null;
        var losses = new LossTracker();
        bool rewound = false;
        int subTicksSinceReRoute = 0;
        void BeforeSubTick()
        {
            reRouted ??= TryReRoute(engine, aircraft, approachRig, c);
            losses.Before(aircraft);
        }

        void AfterSubTick()
        {
            if (reRouted is null)
            {
                return;
            }

            double largestBeforeKts = losses.LargestKts;
            losses.After(aircraft);
            if (losses.LargestKts > largestBeforeKts)
            {
                losses.LargestAt =
                    $"from {losses.LargestFromKts:F2} kt, nose {reRouted.Line.NosePastFt(aircraft):F1} ft past the line, "
                    + $"phase {aircraft.Phases?.CurrentPhase?.Name}";
            }

            if (c.ReArmStartBar && (++subTicksSinceReRoute == SimulationEngine.PhysicsSubTickRate))
            {
                ReArmBar(engine);
            }

            if (c.RewindMidStop && !rewound && (aircraft.GroundSpeed < RewindBelowKts))
            {
                rewound = RewindTaxi(aircraft, clearedBar.NodeId);
            }
        }

        for (int second = 0; (second < ApproachBudgetSeconds) && (reRouted is null); second++)
        {
            StepSecond(engine, BeforeSubTick, AfterSubTick);
        }

        Assert.True(reRouted is not null, $"the {c.Type} never met the re-route condition approaching the runway {CrossedRunway} bar");
        for (int second = 0; (second < StopBudgetSeconds) && !losses.IsAtRest; second++)
        {
            StepSecond(engine, BeforeSubTick, AfterSubTick);
        }

        Assert.True(losses.IsAtRest, $"the {c.Type} never came to rest after the re-route; gs={aircraft.GroundSpeed:F1}");
        AssertOrderStillBinds(c, aircraft);
        Assert.NotNull(reRouted);
        return AtRest(engine, aircraft, reRouted, losses, rewound);
    }

    /// <summary>Logs where the aircraft came to rest after the re-route and how it braked, and returns the run's outcome.</summary>
    private Outcome AtRest(SimulationEngine engine, AircraftState aircraft, ReRouted reRouted, LossTracker losses, bool rewound)
    {
        double nosePastFt = reRouted.Line.NosePastFt(aircraft);
        double centreFt = GeoMath.DistanceNm(aircraft.Position, reRouted.Line.Node.Position) * GeoMath.FeetPerNm;
        _output.WriteLine(
            $"at rest: nose {nosePastFt:F1} ft past the runway {CrossedRunway} hold line, centre "
                + $"{centreFt:F1} ft from bar node {reRouted.Line.Node.Id}, "
                + $"largest one-sub-tick speed loss {losses.LargestKts:F2} kt ({losses.LargestAt}), largest while still rolling "
                + $"{losses.LargestRollingKts:F2} kt, largest stop from {losses.LargestStopFromKts:F2} kt, largest gain "
                + $"{losses.LargestGainKts:F2} kt, phase {aircraft.Phases?.CurrentPhase?.Name}"
        );
        return new Outcome(
            engine,
            aircraft,
            reRouted.Line,
            nosePastFt,
            losses.LargestKts,
            losses.LargestFromKts,
            losses.LargestRollingKts,
            losses.LargestStopFromKts,
            losses.LargestGainKts,
            reRouted.AtOrder.ToStopFt,
            reRouted.AtOrder.TaxiStopFt,
            reRouted.StartsAtBar,
            reRouted.NosePastAtOrderFt,
            reRouted.AtOrder.FirmStopFt,
            rewound,
            HoldLine.Nose(aircraft)
        );
    }

    /// <summary>
    /// Replaces the running taxi phase with one restored from its own snapshot, as a rewind does, after checking the snapshot
    /// carries the start bar the phase is stopping for. False when the aircraft is no longer taxiing.
    /// </summary>
    private static bool RewindTaxi(AircraftState aircraft, int barNodeId)
    {
        if (aircraft.Phases is not { CurrentPhase: TaxiingPhase taxiing } phases)
        {
            return false;
        }

        var dto = (TaxiingPhaseDto)taxiing.ToSnapshot();
        Assert.Equal(barNodeId, dto.PassedStartBarNodeId);
        phases.Phases[phases.CurrentIndex] = TaxiingPhase.FromSnapshot(dto);
        return true;
    }

    /// <summary>Re-arms the runway 33 bar with <c>HS 33</c>, as a controller's hold-short after a crossing clearance.</summary>
    private void ReArmBar(SimulationEngine engine)
    {
        CommandResult holdShort = engine.SendCommand(Callsign, $"HS {CrossedRunway}");
        Assert.True(holdShort.Success, $"HS {CrossedRunway} was rejected: {holdShort.Message}");
        _output.WriteLine($"HS {CrossedRunway}: {holdShort.Message}");
    }

    private static void AssertOrderStillBinds(Case c, AircraftState aircraft)
    {
        switch (c.Order)
        {
            case Order.Hold:
                Assert.True(aircraft.Ground.Hold is { Kind: HoldKind.HoldPosition }, $"the {c.Type} came to rest without its HOLD");
                break;
            case Order.GiveWay:
            case Order.GiveWayToCrossingTraffic:
                Assert.True(aircraft.Ground.Hold is { Kind: HoldKind.GiveWay }, $"the {c.Type} came to rest without its GIVEWAY");
                break;
            case Order.Cross:
            case Order.None:
                break;
        }
    }

    /// <summary>The first route's view of the 33 bar the aircraft is approaching, cleared, and its hold line.</summary>
    private sealed record ApproachRig(AirportGroundLayout Layout, TaxiRoute FirstRoute, int BarSegment, HoldShortPoint ClearedBar, HoldLine Line);

    /// <summary>The re-route: where the aircraft stood when it was given, the hold line on the new route, and whether it starts on the bar.</summary>
    private sealed record ReRouted(Approach AtOrder, HoldLine Line, bool StartsAtBar, double NosePastAtOrderFt);

    /// <summary>
    /// On the sub-tick <paramref name="c"/>'s condition first holds, re-routes the aircraft without the crossing and gives it
    /// the case's order; null on every other sub-tick.
    /// </summary>
    private ReRouted? TryReRoute(SimulationEngine engine, AircraftState aircraft, ApproachRig rig, Case c)
    {
        if ((aircraft.Phases?.CurrentPhase is not TaxiingPhase) || (rig.FirstRoute.CurrentSegmentIndex > rig.BarSegment))
        {
            return null;
        }

        Approach approach = Measure(aircraft, rig, c.Category);
        if ((c.Order == Order.GiveWayToCrossingTraffic) && !IsCrossingTrafficReady(engine, aircraft, approach))
        {
            return null;
        }

        if (!c.When(approach))
        {
            return null;
        }

        if (c.ReRouteAtKts is { } reRouteKts)
        {
            aircraft.IndicatedAirspeed = reRouteKts;
        }

        CommandResult reRoute = engine.SendCommand(Callsign, ReRoute);
        Assert.True(reRoute.Success, $"{ReRoute} was rejected: {reRoute.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        bool startsAtBar = route.Segments[0].FromNodeId == rig.ClearedBar.NodeId;
        HoldLine line;
        string where;
        if (startsAtBar)
        {
            Assert.True(route.GetHoldShortAt(rig.ClearedBar.NodeId) is { IsCleared: false }, $"{ReRoute} did not hold short of the start bar");
            line = rig.Line;
            where = $"route starts on bar node {rig.ClearedBar.NodeId}";
        }
        else
        {
            (int newBarSegment, HoldShortPoint bar) = BarAhead(route);
            Assert.Equal(rig.ClearedBar.NodeId, bar.NodeId);
            Assert.False(bar.IsCleared, $"{ReRoute} left the runway {CrossedRunway} bar cleared");
            line = HoldLine.Through(route, newBarSegment);
            where = $"stop set back {route.HoldShortSetbackNm(newBarSegment, bar) * GeoMath.FeetPerNm:F1} ft from bar node {bar.NodeId}";
        }

        GiveOrder(engine, aircraft, rig.Layout, c.Order);
        double nosePastFt = rig.Line.NosePastFt(aircraft);
        _output.WriteLine(
            $"{ReRoute} + {c.Order} at {aircraft.GroundSpeed:F1} kt, {approach.ToStopFt:F1} ft from the painted stop (taxi-rate stop "
                + $"{approach.TaxiStopFt:F1} ft, firm-rate stop {approach.FirmStopFt:F1} ft, nose {nosePastFt:F1} ft past the line), {where}"
        );
        return new ReRouted(approach, line, startsAtBar, nosePastFt);
    }

    private static Approach Measure(AircraftState aircraft, ApproachRig rig, AircraftCategory category)
    {
        double toStopFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(rig.Layout, rig.FirstRoute, aircraft.Position, rig.ClearedBar);
        double taxiStopFt = StoppingDistanceFt(aircraft.GroundSpeed, CategoryPerformance.TaxiDecelRate(category));
        double firmStopFt = StoppingDistanceFt(aircraft.GroundSpeed, CategoryPerformance.ExpediteExitDecelRate(category));
        bool startsAtBar = rig.Layout.FindNearestNodeForTaxi(aircraft.Position, aircraft.TrueHeading)?.Id == rig.ClearedBar.NodeId;
        double centrePastNodeFt = rig.Line.NosePastFt(aircraft) - (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0);
        return new Approach(toStopFt, taxiStopFt, firmStopFt, startsAtBar, centrePastNodeFt);
    }

    private void GiveOrder(SimulationEngine engine, AircraftState aircraft, AirportGroundLayout layout, Order order)
    {
        switch (order)
        {
            case Order.Hold:
                Assert.True(engine.SendCommand(Callsign, "HOLD").Success);
                break;
            case Order.GiveWay:
                GiveWayToTrafficBehind(engine, aircraft, layout);
                break;
            case Order.GiveWayToCrossingTraffic:
                GiveWayToCrossingTraffic(engine, aircraft, layout);
                break;
            case Order.Cross:
                Assert.True(engine.SendCommand(Callsign, $"CROSS {CrossedRunway}").Success, $"CROSS {CrossedRunway} was rejected");
                break;
            case Order.None:
                break;
        }
    }

    /// <summary>
    /// Starts the crossing traffic on J taxiing once the giver is within <see cref="TrafficTaxiTriggerFt"/> of the 33 bar's
    /// stop, and says whether a GIVEWAY to it would bind now: the traffic rolling and the routes meeting ahead of the giver.
    /// </summary>
    private bool IsCrossingTrafficReady(SimulationEngine engine, AircraftState aircraft, Approach approach)
    {
        AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(Traffic));
        if (!_trafficTaxiing)
        {
            if (approach.ToStopFt > TrafficTaxiTriggerFt)
            {
                return false;
            }

            CommandResult taxi = engine.SendCommand(Traffic, TrafficTaxiOnJ);
            Assert.True(taxi.Success, $"{TrafficTaxiOnJ} was rejected: {taxi.Message}");
            _trafficTaxiing = true;
        }

        return (traffic.GroundSpeed > TrafficRollingKts) && (GroundConflictDetector.GiveWayStop(aircraft, traffic, out _) is not null);
    }

    /// <summary>
    /// Tells the aircraft to give way to the crossing traffic on J, whose give-way point lies past the uncleared runway 33
    /// bar on the re-routed route.
    /// </summary>
    private void GiveWayToCrossingTraffic(SimulationEngine engine, AircraftState aircraft, AirportGroundLayout layout)
    {
        AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft(Traffic));
        CommandResult giveWay = engine.SendCommand(Callsign, $"GIVEWAY {Traffic}");
        Assert.True(giveWay.Success, $"GIVEWAY {Traffic} was rejected: {giveWay.Message}");
        (int NodeId, double ToStopFt)? giveWayPoint = GroundConflictDetector.GiveWayStop(aircraft, traffic, out string? noStopReason);
        Assert.True(giveWayPoint is not null, $"GIVEWAY {Traffic} has no give-way point: {noStopReason}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        (_, HoldShortPoint bar) = BarAhead(route);
        double toBarStopFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, bar);
        _output.WriteLine(
            $"GIVEWAY {Traffic}: give-way point {giveWayPoint.Value.ToStopFt:F1} ft ahead at node {giveWayPoint.Value.NodeId}, the uncleared "
                + $"runway {CrossedRunway} bar's stop {toBarStopFt:F1} ft, traffic at {traffic.GroundSpeed:F1} kt"
        );
        Assert.True(giveWayPoint.Value.ToStopFt > toBarStopFt, $"the give-way point is not past the runway {CrossedRunway} bar");
    }

    /// <summary>
    /// Places a stationary C172 with no route <see cref="TrafficBehindFt"/> behind the aircraft on its heading and tells the
    /// aircraft to give way to it. Behind on the same heading keeps the GIVEWAY bound (the traffic is still to pass), and with
    /// no route of its own the traffic leaves the aircraft no give-way point, so the hold stops it where it is.
    /// </summary>
    private void GiveWayToTrafficBehind(SimulationEngine engine, AircraftState aircraft, AirportGroundLayout layout)
    {
        var reciprocal = new TrueHeading((aircraft.TrueHeading.Degrees + 180.0) % 360.0);
        var traffic = new AircraftState
        {
            Callsign = Traffic,
            AircraftType = Piston,
            Position = GeoMath.ProjectPoint(aircraft.Position, reciprocal, TrafficBehindFt / GeoMath.FeetPerNm),
            TrueHeading = aircraft.TrueHeading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
            },
            Phases = new PhaseList(),
        };
        traffic.Phases.Add(new HoldingInPositionPhase());
        traffic.Phases.Start(CommandDispatcher.BuildMinimalContext(traffic, layout));
        traffic.Ground.Layout = layout;
        engine.World.AddAircraft(traffic);

        CommandResult giveWay = engine.SendCommand(Callsign, $"GIVEWAY {Traffic}");
        Assert.True(giveWay.Success, $"GIVEWAY {Traffic} was rejected: {giveWay.Message}");
        Assert.Null(GroundConflictDetector.GiveWayStop(aircraft, traffic, out string? noStopReason));
        _output.WriteLine($"GIVEWAY {Traffic}: no give-way point ({noStopReason})");
    }

    /// <summary>Where the aircraft came to rest after the taxiway-bar re-route, and how it braked there.</summary>
    private sealed record TaxiwayOutcome(double CentrePastStopFt, double LargestRollingLossKts, double LargestStopFromKts);

    /// <summary>
    /// Taxis the B738 on <see cref="FirstTaxi"/> across runway 33 and along C toward the C/E junction and, on the first sub-tick
    /// <paramref name="when"/> holds against taxiway E's painted stop, re-routes it <see cref="TaxiwayReRoute"/>; then runs it
    /// to rest. Null when the KOAK layout is unavailable.
    /// </summary>
    private TaxiwayOutcome? RunTaxiwayBar(Func<Approach, bool> when)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            _output.WriteLine("SKIP: KOAK layout unavailable");
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(Scenario, 1, MagneticDeclination.EvaluationDateUtc);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        Assert.True(engine.SendCommand(Callsign, FirstTaxi).Success);
        engine.TickOneSecond();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        TaxiRoute firstRoute = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int farBarSegment = LastBarSegment(firstRoute);
        TaxiRoute? baseRoute = null;
        ApproachRig? rig = null;

        HoldShortPoint? reRouted = null;
        LossTracker? losses = null;
        void BeforeSubTick()
        {
            bool taxiing = aircraft.Phases?.CurrentPhase is TaxiingPhase;
            if ((rig is null) && taxiing && (firstRoute.CurrentSegmentIndex > farBarSegment))
            {
                (baseRoute, rig) = ReRouteAndProbe(engine, aircraft, layout);
            }
            else if (
                (rig is not null)
                && (baseRoute is not null)
                && (reRouted is null)
                && taxiing
                && (baseRoute.CurrentSegmentIndex <= rig.BarSegment)
            )
            {
                rig.FirstRoute.CurrentSegmentIndex = baseRoute.CurrentSegmentIndex;
                reRouted = TryTaxiwayReRoute(engine, aircraft, rig, when);
            }

            if (reRouted is not null)
            {
                losses ??= new LossTracker();
                losses.Before(aircraft);
            }
        }

        void AfterSubTick()
        {
            if (losses is null)
            {
                return;
            }

            losses.After(aircraft);
        }

        for (int second = 0; (second < ApproachBudgetSeconds) && (reRouted is null); second++)
        {
            StepSecond(engine, BeforeSubTick, AfterSubTick);
        }

        Assert.True(reRouted is not null, $"the {Jet} never met the re-route condition approaching taxiway {TaxiwayTarget}");
        for (int second = 0; (second < StopBudgetSeconds) && (losses?.IsAtRest != true); second++)
        {
            StepSecond(engine, BeforeSubTick, AfterSubTick);
        }

        Assert.NotNull(losses);
        Assert.True(losses.IsAtRest, $"the {Jet} never came to rest after {TaxiwayReRoute}; gs={aircraft.GroundSpeed:F1}");
        double centrePastFt = CentrePastStopFt(aircraft, reRouted);
        _output.WriteLine(
            $"at rest: centre {centrePastFt:F1} ft past taxiway {TaxiwayTarget}'s painted stop, largest loss while rolling "
                + $"{losses.LargestRollingKts:F2} kt, largest stop from {losses.LargestStopFromKts:F2} kt, "
                + $"phase {aircraft.Phases?.CurrentPhase?.Name}"
        );
        return new TaxiwayOutcome(centrePastFt, losses.LargestRollingKts, losses.LargestStopFromKts);
    }

    /// <summary>The index of the segment ending at the last runway 33 bar on <paramref name="route"/>: the crossing's far bar.</summary>
    private static int LastBarSegment(TaxiRoute route)
    {
        int last = route.Segments.FindLastIndex(segment =>
            (route.GetHoldShortAt(segment.ToNodeId) is { } bar) && HoldShortAnnotator.TargetMatches(bar.TargetName, CrossedRunway)
        );
        Assert.True(last >= 0, $"no runway {CrossedRunway} bar on the route {route.ToSummary()}");
        return last;
    }

    /// <summary>
    /// Once across runway 33, re-routes the aircraft <see cref="ReRoute"/> — the same taxi with no hold short — and probes that
    /// route for taxiway E's bar (<see cref="ProbeTaxiwayBar"/>): the route the aircraft now taxis, and the rig measured on it.
    /// </summary>
    private static (TaxiRoute BaseRoute, ApproachRig Rig) ReRouteAndProbe(SimulationEngine engine, AircraftState aircraft, AirportGroundLayout layout)
    {
        CommandResult reRoute = engine.SendCommand(Callsign, ReRoute);
        Assert.True(reRoute.Success, $"{ReRoute} was rejected: {reRoute.Message}");
        TaxiRoute baseRoute = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        (TaxiRoute probe, int barSegment, HoldShortPoint bar) = ProbeTaxiwayBar(layout, baseRoute, aircraft);
        return (baseRoute, new ApproachRig(layout, probe, barSegment, bar, HoldLine.Through(baseRoute, barSegment)));
    }

    /// <summary>
    /// Taxiway E's bar as <see cref="TaxiwayReRoute"/> would place it, worked out on a copy of <paramref name="route"/> without
    /// touching the aircraft's: the copy, the index of the segment ending at the bar's node, and the bar with its painted stop.
    /// </summary>
    private static (TaxiRoute Probe, int BarSegment, HoldShortPoint Bar) ProbeTaxiwayBar(
        AirportGroundLayout layout,
        TaxiRoute route,
        AircraftState aircraft
    )
    {
        var probe = new TaxiRoute { Segments = [.. route.Segments], HoldShortPoints = [] };
        var target = HoldShortTarget.Parse(TaxiwayTarget);
        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, probe, target);
        Assert.Equal(ExplicitHoldShortOutcome.Add, plan.Outcome);
        HoldShortAnnotator.ApplyExplicitHoldShort(probe, plan, target);
        HoldShortAnnotator.ComputeHoldShortPositions(layout, probe, AircraftLength.ResolveFt(aircraft.AircraftType));
        HoldShortPoint bar = Assert.IsType<HoldShortPoint>(probe.GetHoldShortAt(plan.NodeId));
        int barSegment = probe.Segments.FindIndex(segment => segment.ToNodeId == bar.NodeId);
        Assert.True(barSegment >= 0, $"no segment of {route.ToSummary()} ends at taxiway {TaxiwayTarget}'s bar node {bar.NodeId}");
        return (probe, barSegment, bar);
    }

    /// <summary>
    /// On the sub-tick <paramref name="when"/> first holds against taxiway E's probed painted stop, re-routes the aircraft
    /// <see cref="TaxiwayReRoute"/> and returns the new route's uncleared bar of E, which must lie on the probed node. Null on
    /// every other sub-tick.
    /// </summary>
    private HoldShortPoint? TryTaxiwayReRoute(SimulationEngine engine, AircraftState aircraft, ApproachRig rig, Func<Approach, bool> when)
    {
        Approach measured = Measure(aircraft, rig, AircraftCategory.Jet);
        if (!when(measured))
        {
            return null;
        }

        CommandResult reRoute = engine.SendCommand(Callsign, TaxiwayReRoute);
        Assert.True(reRoute.Success, $"{TaxiwayReRoute} was rejected: {reRoute.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int barNodeId = rig.ClearedBar.NodeId;
        _output.WriteLine(
            $"probe bar node {rig.ClearedBar.NodeId}; new route {route.ToSummary()} from node {route.Segments[0].FromNodeId}, holds short at "
                + $"[{string.Join(", ", route.HoldShortPoints.Select(hs => $"{hs.NodeId}:{hs.TargetName}:{hs.Reason}:{hs.IsCleared}"))}]"
        );
        HoldShortPoint? bar = route.GetHoldShortAt(barNodeId);
        Assert.True(
            (bar is { IsCleared: false }) && HoldShortAnnotator.TargetMatches(bar.TargetName, TaxiwayTarget),
            $"{TaxiwayReRoute} did not hold short of taxiway {TaxiwayTarget} at node {barNodeId}"
        );
        _output.WriteLine(
            $"{TaxiwayReRoute} at {aircraft.GroundSpeed:F1} kt, {measured.ToStopFt:F1} ft from the probed stop "
                + $"({TaxiingPhase.AlongRouteDistanceToHoldShortFt(rig.Layout, route, aircraft.Position, bar):F1} ft on the new route, centre "
                + $"{CentrePastStopFt(aircraft, bar):F1} ft past it; taxi-rate stop {measured.TaxiStopFt:F1} ft, "
                + $"firm-rate stop {measured.FirmStopFt:F1} ft)"
        );
        return bar;
    }

    /// <summary>
    /// How far (ft) the aircraft's centre is past <paramref name="bar"/>'s painted stop — where the centre stops with the nose at
    /// the line — measured along its heading; negative when short.
    /// </summary>
    private static double CentrePastStopFt(AircraftState aircraft, HoldShortPoint bar)
    {
        var stop = new LatLon(Assert.IsType<double>(bar.Latitude), Assert.IsType<double>(bar.Longitude));
        double distFt = GeoMath.DistanceNm(aircraft.Position, stop) * GeoMath.FeetPerNm;
        double offRad = GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, stop)) * Math.PI / 180.0;
        return -(distFt * Math.Cos(offRad));
    }

    /// <summary>The first route bar ahead on runway 33, with the index of the segment ending at it.</summary>
    private static (int SegmentIndex, HoldShortPoint Bar) BarAhead(TaxiRoute route)
    {
        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            if ((route.GetHoldShortAt(route.Segments[i].ToNodeId) is { } bar) && HoldShortAnnotator.TargetMatches(bar.TargetName, CrossedRunway))
            {
                return (i, bar);
            }
        }

        Assert.Fail($"no runway {CrossedRunway} bar ahead on the route {route.ToSummary()}");
        return default;
    }

    private static double StoppingDistanceFt(double speedKts, double decelKtsPerSec) =>
        (speedKts * speedKts) / (2.0 * decelKtsPerSec) * (GeoMath.FeetPerNm / 3600.0);

    /// <summary>One sim-second, stepped a physics sub-tick at a time, calling the hooks around each sub-tick.</summary>
    private static void StepSecond(SimulationEngine engine, Action beforeSubTick, Action afterSubTick)
    {
        engine.BeginSecond();
        engine.OpenSecond(engine.BareHost);
        engine.RunPrePhysics(engine.BareHost);
        for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
        {
            beforeSubTick();
            engine.RunPhysicsSubTick(1.0 / SimulationEngine.PhysicsSubTickRate, sub);
            afterSubTick();
        }

        engine.RunPostPhysics(engine.BareHost);
        engine.RunEndOfSecond(engine.BareHost);
    }

    /// <summary>The hold line through a bar node, square to the route's approach to it.</summary>
    private sealed record HoldLine(GroundNode Node, double ApproachDeg)
    {
        /// <summary>The line through the end node of <paramref name="route"/>'s segment <paramref name="barSegment"/>.</summary>
        internal static HoldLine Through(TaxiRoute route, int barSegment)
        {
            GroundNode node = route.Segments[barSegment].Edge.ToNode;
            LatLon back = route.Segments[barSegment].Edge.FromNode.Position;
            for (int i = barSegment; i >= 0; i--)
            {
                back = route.Segments[i].Edge.FromNode.Position;
                if (GeoMath.DistanceNm(node.Position, back) * GeoMath.FeetPerNm >= ApproachBaseFt)
                {
                    break;
                }
            }

            return new HoldLine(node, GeoMath.BearingTo(node.Position, back));
        }

        /// <summary>How far (ft) the aircraft's nose is past the line, measured along the approach; negative when short.</summary>
        internal double NosePastFt(AircraftState aircraft)
        {
            LatLon nose = Nose(aircraft);
            double distFt = GeoMath.DistanceNm(Node.Position, nose) * GeoMath.FeetPerNm;
            double offRad = GeoMath.SignedBearingDifference(ApproachDeg, GeoMath.BearingTo(Node.Position, nose)) * Math.PI / 180.0;
            return -(distFt * Math.Cos(offRad));
        }

        /// <summary>The aircraft's nose: its centre projected half its length ahead on its heading.</summary>
        internal static LatLon Nose(AircraftState aircraft) =>
            GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm);
    }
}

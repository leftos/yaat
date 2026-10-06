using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A taxiing aircraft told <c>GIVEWAY</c> keeps taxiing to the give-way point — where its centre comes within wingtip
/// clearance (both half-spans plus <c>GroundOutlineSweep.WingtipBufferFt</c>) of the traffic's track through the first
/// node the two routes share — and brakes at its category's taxi brake rate to stop there, braking harder only when told
/// inside its taxi-rate stopping distance. Stopped there, it leaves the traffic the room the conflict detector wants, so the
/// traffic crosses without being stopped, and the give-way releases behind it.
///
/// <para>The FOLLOW montage's H2 clip (<c>tools/montage/follow/H2</c>): at KOAK, N738SP taxis from GA16 via F, C, B to
/// runway 28R while N52417, told to taxi at t=36, taxis north on K across F towards the east GA ramp; at t=56 N738SP is
/// told <c>GIVEWAY N52417</c> and gives way on F at the F/K/L junction.</para>
/// </summary>
public class GiveWayStopBrakingTests(ITestOutputHelper output)
{
    private const int LeadTaxiSecond = 36;
    private const int GiveWaySecond = 56;
    private const int StopBudgetSeconds = 40;
    private const int LateBudgetSeconds = 60;
    private const int ResumeBudgetSeconds = 180;
    private const double StationaryKts = 0.05;
    private const double MovingKts = 1.0;

    /// <summary>How far (ft) past the clearance the stop may leave the centre.</summary>
    private const double StopToleranceFt = 30.0;

    private const double FeetPerSecondPerKt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>The long type the nose test holds: half its 146 ft fuselage reaches well past its centre's stop.</summary>
    private const string LongType = "A321";

    private const int LongTypeBudgetSeconds = 300;

    /// <summary>Points sampled along a fillet's curve when measuring how far a point is from it.</summary>
    private const int ArcSamples = 64;

    private const string Scenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6QC",
          "name": "Montage H2 | Give way to traffic crossing ahead on the taxiway",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH02N52417G8F0L1W5T7R9NB",
              "aircraftId": "N52417",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.73075, "lon": -122.22539 }, "heading": 9 },
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
            {
              "id": "01JH02N738SPG8F0L1W5T7R9ND",
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

    /// <summary>The H2 clip's <c>script.txt</c>.</summary>
    private static readonly (int Second, string Callsign, string Command)[] Script =
    [
        (1, KoakFollowClip.Lead, "CAINH"),
        (1, KoakFollowClip.Follower, "CAINH"),
        (2, KoakFollowClip.Follower, "TAXI F C B CROSS 33 RWY 28R"),
        (LeadTaxiSecond, KoakFollowClip.Lead, "TAXI K D @GA7 CROSS 33"),
        (GiveWaySecond, KoakFollowClip.Follower, $"GIVEWAY {KoakFollowClip.Lead}"),
    ];

    /// <summary>
    /// N738SP keeps taxiing after the GIVEWAY and brakes at the piston taxi rate to stop with its centre clear of N52417's
    /// track through the F/K junction by the pair's wingtip clearance, and by no more than <see cref="StopToleranceFt"/>
    /// beyond it. N52417 then crosses the junction without the conflict detector stopping it, and N738SP taxis on behind.
    /// </summary>
    [Fact]
    public void H2_GiveWay_StopsClearOfTheCrossingTrack()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .EnableCategory("FlightPhysics", LogLevel.Information)
            .InitializeSimLog();
        AircraftState giver = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        double requiredFt = RequiredClearanceFt();
        var speeds = new List<(int Second, double SpeedKts)>();
        JunctionTrack? junction = null;
        int stoppedAt = -1;
        double clearanceAtRestFt = double.NaN;
        double minClearanceFt = double.MaxValue;
        double leadMinSpeedKts = double.MaxValue;
        int leadCrossedAt = -1;
        int resumedAt = KoakFollowClip.RunScript(
            engine,
            output,
            Script,
            GiveWaySecond + ResumeBudgetSeconds,
            second =>
            {
                if (second < GiveWaySecond)
                {
                    return false;
                }

                junction ??= JunctionTrack.Capture(giver, lead);
                double clearanceFt = junction.ClearanceFt(giver.Position);
                if (leadCrossedAt < 0)
                {
                    leadMinSpeedKts = Math.Min(leadMinSpeedKts, lead.GroundSpeed);
                    leadCrossedAt = junction.LeadIsPast(lead) ? second : -1;
                }

                output.WriteLine(
                    $"t={second} {giver.Phases?.CurrentPhase?.Name} gs={giver.GroundSpeed:F1} hold={giver.Ground.Hold?.Kind} "
                        + $"off-track={clearanceFt:F1} ft decelRate={giver.Targets.DesiredDecelRate} | lead gs={lead.GroundSpeed:F1} "
                        + $"limit={lead.Ground.SpeedLimit?.ToString("F1")}"
                );
                if (stoppedAt < 0)
                {
                    minClearanceFt = Math.Min(minClearanceFt, clearanceFt);
                    speeds.Add((second, giver.GroundSpeed));
                    if ((second > GiveWaySecond) && (giver.GroundSpeed < StationaryKts))
                    {
                        Assert.True(giver.Ground.Hold is { Kind: HoldKind.GiveWay }, $"N738SP came to rest at t={second} without its give-way hold");
                        stoppedAt = second;
                        clearanceAtRestFt = clearanceFt;
                        LogStop(giver, junction, clearanceFt, requiredFt, second);
                    }

                    return false;
                }

                return (leadCrossedAt > 0) && (giver.Ground.Hold is null) && (giver.GroundSpeed > MovingKts);
            }
        );

        Assert.True(stoppedAt > 0, $"N738SP never came to rest after GIVEWAY; gs={giver.GroundSpeed:F1}");
        Assert.True(stoppedAt <= GiveWaySecond + StopBudgetSeconds, $"N738SP took until t={stoppedAt} to stop");
        (double lossKts, int lossAt) = KoakFollowClip.LargestSpeedLossPerSecond(speeds);
        output.WriteLine($"peak one-second speed loss {lossKts:F2} kt ending t={lossAt}; closest approach to the track {minClearanceFt:F1} ft");
        AssertNoFasterThanTheTaxiBrakeRate(lossKts, lossAt);
        Assert.InRange(clearanceAtRestFt, requiredFt, requiredFt + StopToleranceFt);
        Assert.True(minClearanceFt >= requiredFt, $"N738SP came within {minClearanceFt:F1} ft of N52417's track (needs {requiredFt:F1})");

        output.WriteLine(
            $"N52417 crossed the junction at t={leadCrossedAt}, slowest {leadMinSpeedKts:F1} kt on the way; N738SP resumed at t={resumedAt}"
        );
        Assert.True(leadCrossedAt > 0, "N52417 never crossed the F/K junction");
        Assert.True(leadMinSpeedKts > StationaryKts, $"N52417 was stopped short of the junction (slowest {leadMinSpeedKts:F2} kt)");
        Assert.True(resumedAt > 0, $"N738SP never resumed its taxi after N52417 crossed: hold={giver.Ground.Hold?.Kind}");
        Assert.IsType<TaxiingPhase>(giver.Phases?.CurrentPhase);
    }

    /// <summary>
    /// GIVEWAY issued when N738SP is already inside its taxi-rate stopping distance of the give-way point: it brakes harder
    /// than the taxi rate, at no more than the piston firm rate, and its centre never comes inside the clearance off
    /// N52417's track.
    /// </summary>
    [Fact]
    public void GiveWay_InsideBrakingDistance_UsesFirmRateAndNeverPassesThePoint()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        AircraftState giver = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        double requiredFt = RequiredClearanceFt();
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston);
        var speeds = new List<(int Second, double SpeedKts)>();
        JunctionTrack? junction = null;
        int giveWayAt = -1;
        double minClearanceFt = double.MaxValue;
        int stoppedAt = KoakFollowClip.RunScript(
            engine,
            output,
            [.. Script.Where(line => line.Second != GiveWaySecond)],
            GiveWaySecond + StopBudgetSeconds + LateBudgetSeconds,
            second =>
            {
                if (second < GiveWaySecond)
                {
                    return false;
                }

                junction ??= JunctionTrack.Capture(giver, lead);
                double clearanceFt = junction.ClearanceFt(giver.Position);
                if (giveWayAt < 0)
                {
                    double toStopFt = Assert.IsType<(int, double)>(GroundConflictDetector.GiveWayStop(giver, lead, out _)).Item2;
                    double taxiStopFt = (giver.GroundSpeed * giver.GroundSpeed) / (2.0 * taxiRate) * FeetPerSecondPerKt;
                    if (toStopFt >= taxiStopFt)
                    {
                        return false;
                    }

                    giveWayAt = second;
                    output.WriteLine($"t={second}: GIVEWAY at gs={giver.GroundSpeed:F1}, {toStopFt:F1} ft to the point, taxi stop {taxiStopFt:F1}");
                    Assert.True(engine.SendCommand(KoakFollowClip.Follower, $"GIVEWAY {KoakFollowClip.Lead}").Success);
                    speeds.Add((second, giver.GroundSpeed));
                    return false;
                }

                minClearanceFt = Math.Min(minClearanceFt, clearanceFt);
                speeds.Add((second, giver.GroundSpeed));
                output.WriteLine($"t={second} gs={giver.GroundSpeed:F1} off-track={clearanceFt:F1} decelRate={giver.Targets.DesiredDecelRate}");
                return giver.GroundSpeed < StationaryKts;
            }
        );

        Assert.True(giveWayAt > 0, "N738SP never came inside its taxi-rate stopping distance of the give-way point");
        Assert.True(stoppedAt > 0, $"N738SP never came to rest after the late GIVEWAY; gs={giver.GroundSpeed:F1}");
        (double lossKts, int atSecond) = KoakFollowClip.LargestSpeedLossPerSecond(speeds);
        double firmLossKts = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        output.WriteLine($"largest one-second speed loss {lossKts:F2} kt ending t={atSecond}; closest approach to the track {minClearanceFt:F1} ft");
        Assert.True(lossKts > KoakFollowClip.MaxPistonSpeedLossPerSecondKts, $"N738SP braked no harder than the taxi rate ({lossKts:F2} kt/s)");
        Assert.True(lossKts <= firmLossKts + 1e-6, $"N738SP lost {lossKts:F2} kt in a second, more than the firm rate allows");
        Assert.True(minClearanceFt >= requiredFt, $"N738SP passed the give-way point: {minClearanceFt:F1} ft off the track (needs {requiredFt:F1})");
    }

    /// <summary>
    /// GIVEWAY issued while N738SP is already inside the clearance off N52417's track (its give-way point is where it
    /// stands): a GIVEWAY has no marking to protect, so it brakes at no more than the piston firm rate and stops where that
    /// takes it, never dropping to rest in one tick.
    /// </summary>
    [Fact]
    public void GiveWay_InsideTheClearance_BrakesAtTheFirmRateWithoutStoppingDead()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        AircraftState giver = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        var speeds = new List<(int Second, double SpeedKts)>();
        int giveWayAt = -1;
        int stoppedAt = KoakFollowClip.RunScript(
            engine,
            output,
            [.. Script.Where(line => line.Second != GiveWaySecond)],
            GiveWaySecond + StopBudgetSeconds + LateBudgetSeconds,
            second =>
            {
                if (second < GiveWaySecond)
                {
                    return false;
                }

                if (giveWayAt < 0)
                {
                    if ((giver.GroundSpeed <= MovingKts) || (GroundConflictDetector.GiveWayStop(giver, lead, out _) is not { ToStopFt: 0.0 }))
                    {
                        return false;
                    }

                    giveWayAt = second;
                    output.WriteLine($"t={second}: GIVEWAY inside the clearance at gs={giver.GroundSpeed:F1}");
                    Assert.True(engine.SendCommand(KoakFollowClip.Follower, $"GIVEWAY {KoakFollowClip.Lead}").Success);
                    speeds.Add((second, giver.GroundSpeed));
                    return false;
                }

                speeds.Add((second, giver.GroundSpeed));
                output.WriteLine($"t={second} gs={giver.GroundSpeed:F1} decelRate={giver.Targets.DesiredDecelRate}");
                return giver.GroundSpeed < StationaryKts;
            }
        );

        Assert.True(giveWayAt > 0, "N738SP was never moving inside the clearance off N52417's track with the junction still ahead");
        Assert.True(stoppedAt > 0, $"N738SP never came to rest after the GIVEWAY; gs={giver.GroundSpeed:F1}");
        (double lossKts, int atSecond) = KoakFollowClip.LargestSpeedLossPerSecond(speeds);
        double firmLossKts = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        output.WriteLine($"largest one-second speed loss {lossKts:F2} kt ending t={atSecond}; firm rate allows {firmLossKts:F2}");
        Assert.True(lossKts <= firmLossKts + 1e-6, $"N738SP lost {lossKts:F2} kt in the second ending t={atSecond}, more than the firm rate allows");
    }

    /// <summary>
    /// An A321 in N738SP's place, told GIVEWAY once the junction is ahead with room to brake at the taxi rate, comes to rest
    /// with its centre both half-spans plus <see cref="GroundOutlineSweep.WingtipBufferFt"/> off N52417's track and its
    /// nose — half its fuselage ahead of the centre — N52417's half-span plus the buffer off it: a long type stops farther
    /// back than its centre alone needs.
    /// </summary>
    [Fact]
    public void LongType_GiveWay_StopsWithNoseAndCentreClearOfTheCrossingTrack()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, WithHeldType(LongType));
        if (engine is null)
        {
            return;
        }

        AircraftState giver = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        double leadHalfSpanFt = HalfSpanFt("C172");
        double centreRequiredFt = HalfSpanFt(LongType) + leadHalfSpanFt + GroundOutlineSweep.WingtipBufferFt;
        double noseRequiredFt = leadHalfSpanFt + GroundOutlineSweep.WingtipBufferFt;
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        JunctionTrack? junction = null;
        int giveWayAt = -1;
        int stoppedAt = KoakFollowClip.RunScript(
            engine,
            output,
            [.. Script.Where(line => line.Second != GiveWaySecond)],
            LongTypeBudgetSeconds,
            second =>
            {
                if (giveWayAt < 0)
                {
                    double taxiStopFt = (giver.GroundSpeed * giver.GroundSpeed) / (2.0 * taxiRate) * FeetPerSecondPerKt;
                    if (
                        (second <= LeadTaxiSecond)
                        || (giver.GroundSpeed <= MovingKts)
                        || (GroundConflictDetector.GiveWayStop(giver, lead, out _) is not { } stop)
                        || (stop.ToStopFt <= taxiStopFt)
                    )
                    {
                        return false;
                    }

                    junction = JunctionTrack.Capture(giver, lead);
                    giveWayAt = second;
                    output.WriteLine($"t={second}: GIVEWAY at gs={giver.GroundSpeed:F1}, {stop.ToStopFt:F1} ft to the point");
                    Assert.True(engine.SendCommand(KoakFollowClip.Follower, $"GIVEWAY {KoakFollowClip.Lead}").Success);
                    return false;
                }

                output.WriteLine(
                    $"t={second} gs={giver.GroundSpeed:F1} centre={junction!.ClearanceFt(giver.Position):F1} ft "
                        + $"nose={junction.ClearanceFt(Nose(giver)):F1} ft"
                );
                return giver.GroundSpeed < StationaryKts;
            }
        );

        Assert.True(giveWayAt > 0, $"the {LongType} never had the F/K junction ahead with room to brake at the taxi rate");
        Assert.True(stoppedAt > 0, $"the {LongType} never came to rest after GIVEWAY; gs={giver.GroundSpeed:F1}");
        Assert.True(giver.Ground.Hold is { Kind: HoldKind.GiveWay }, $"the {LongType} came to rest without its give-way hold");
        double centreFt = junction!.ClearanceFt(giver.Position);
        double noseFt = junction.ClearanceFt(Nose(giver));
        output.WriteLine(
            $"at rest t={stoppedAt}: centre {centreFt:F1} ft (needs {centreRequiredFt:F1}), nose {noseFt:F1} ft (needs {noseRequiredFt:F1})"
        );
        Assert.True(
            centreFt >= centreRequiredFt,
            $"the {LongType}'s centre stopped {centreFt:F1} ft off N52417's track (needs {centreRequiredFt:F1})"
        );
        Assert.True(noseFt >= noseRequiredFt, $"the {LongType}'s nose stopped {noseFt:F1} ft off N52417's track (needs {noseRequiredFt:F1})");
    }

    /// <summary>The H2 scenario with N738SP flying <paramref name="type"/> instead of a C172.</summary>
    private static string WithHeldType(string type)
    {
        JsonNode scenario = Assert.IsAssignableFrom<JsonNode>(JsonNode.Parse(Scenario));
        JsonNode held = Assert.Single(scenario["aircraft"]!.AsArray(), a => (string?)a?["aircraftId"] == KoakFollowClip.Follower)!;
        held["aircraftType"] = type;
        held["flightplan"]!["aircraftType"] = type;
        return scenario.ToJsonString();
    }

    private static double HalfSpanFt(string type) => Assert.IsType<double>(FaaAircraftDatabase.Get(type)?.WingspanFt) / 2.0;

    /// <summary>The aircraft's nose: its centre projected half its fuselage length ahead along its heading.</summary>
    private static LatLon Nose(AircraftState aircraft) =>
        GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm);

    /// <summary>
    /// N738SP on its H2 taxi (<c>F C B CROSS 33 RWY 28R</c>) is told GIVEWAY to N52417, moved onto taxiway J north-east of
    /// C and taxiing J across C to runway 28R, while N738SP rolls along C toward the runway 33 crossing. The C/J junction lies
    /// just past the crossing, so the give-way point is beyond the runway 33 bars, which its CROSS cleared: nodes an
    /// arrival under a hold stops at. It brakes to the first of them rather than to the farther give-way point, and arrives
    /// there with nothing to drop: it never loses more speed in a second than the piston firm rate allows.
    /// </summary>
    [Fact]
    public void GiveWay_ClearedBarBeforeTheGiveWayPoint_BrakesToItWithoutStoppingDead()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, WithLeadOnJ());
        if (engine is null)
        {
            return;
        }

        AircraftState giver = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(giver.Ground.Layout);
        GroundNode firstBar = layout.Nodes[ClearedBarNodeId];
        var speeds = new List<(int Second, double SpeedKts)>();
        double approachDeg = double.NaN;
        int leadTaxiAt = -1;
        int giveWayAt = -1;
        int stoppedAt = KoakFollowClip.RunScript(
            engine,
            output,
            [.. Script.Where(line => (line.Second != GiveWaySecond) && (line.Second != LeadTaxiSecond))],
            LongTypeBudgetSeconds,
            second =>
            {
                if (leadTaxiAt < 0)
                {
                    if (GeoMath.DistanceNm(giver.Position, firstBar.Position) * GeoMath.FeetPerNm > LeadTaxiTriggerFt)
                    {
                        return false;
                    }

                    leadTaxiAt = second;
                    Assert.True(engine.SendCommand(KoakFollowClip.Lead, LeadTaxiOnJ).Success);
                    return false;
                }

                if (giveWayAt < 0)
                {
                    if (
                        (giver.GroundSpeed <= MovingKts)
                        || (lead.GroundSpeed <= LeadRollingKts)
                        || (GroundConflictDetector.GiveWayStop(giver, lead, out _) is not { } stop)
                        || (HoldShortNodeBefore(giver, stop.NodeId) is not { } holdShortNodeId)
                    )
                    {
                        return false;
                    }

                    giveWayAt = second;
                    Assert.Equal(ClearedBarNodeId, holdShortNodeId);
                    approachDeg = ApproachBearingDeg(firstBar, giver.Position);
                    output.WriteLine(
                        $"t={second}: GIVEWAY at gs={giver.GroundSpeed:F1}, give-way point {stop.ToStopFt:F1} ft ahead at junction {stop.NodeId}, "
                            + $"hold-short node {holdShortNodeId} "
                            + $"(cleared={giver.Ground.AssignedTaxiRoute?.GetHoldShortAt(holdShortNodeId)?.IsCleared}) "
                            + "on the way"
                    );
                    Assert.True(engine.SendCommand(KoakFollowClip.Follower, $"GIVEWAY {KoakFollowClip.Lead}").Success);
                    speeds.Add((second, giver.GroundSpeed));
                    return false;
                }

                speeds.Add((second, giver.GroundSpeed));
                output.WriteLine(
                    $"t={second} gs={giver.GroundSpeed:F1} seg={giver.Ground.AssignedTaxiRoute?.CurrentSegmentIndex} hold={giver.Ground.Hold?.Kind} "
                        + $"{giver.Phases?.CurrentPhase?.Name} limit={giver.Ground.SpeedLimit?.ToString("F1")} | lead gs={lead.GroundSpeed:F1}"
                );
                return giver.GroundSpeed < StationaryKts;
            }
        );

        Assert.True(leadTaxiAt > 0, $"N738SP never came within {LeadTaxiTriggerFt:F0} ft of the runway 33 bar on C (node {ClearedBarNodeId})");
        Assert.True(giveWayAt > 0, "N738SP never had a hold-short node on its route before the give-way point");
        Assert.True(stoppedAt > 0, $"N738SP never came to rest after the GIVEWAY; gs={giver.GroundSpeed:F1}");
        (double lossKts, int atSecond) = KoakFollowClip.LargestSpeedLossPerSecond(speeds);
        double firmLossKts = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        output.WriteLine($"largest one-second speed loss {lossKts:F2} kt ending t={atSecond}; firm rate allows {firmLossKts:F2}");
        Assert.True(lossKts <= firmLossKts + 1e-6, $"N738SP lost {lossKts:F2} kt in the second ending t={atSecond}, more than the firm rate allows");
        double nosePastFt = NosePastHoldLineFt(Nose(giver), firstBar, approachDeg);
        output.WriteLine($"at rest t={stoppedAt}: nose {nosePastFt:F1} ft past the runway 33 hold line (node {ClearedBarNodeId})");
        Assert.True(nosePastFt <= 0.0, $"N738SP's nose came to rest {nosePastFt:F1} ft over the runway 33 holding-position marking");
    }

    /// <summary>
    /// The same crossing, with the GIVEWAY issued once N738SP's nose is past the runway 33 hold line on C (node 518) but its
    /// centre has not reached the bar node, so it is still taxiing toward the crossing its CROSS cleared. The painted stop is
    /// behind it, so it enters the crossing rather than braking to rest between the hold line and the runway (AIM 4-3-21.a),
    /// the GIVEWAY waits until its tail is past the far holding-position marking, and it comes to rest with no part between
    /// the two markings. It never loses more speed in a second than the piston firm rate allows, outside the seconds the
    /// ground-conflict speed limit governs it (the crossing meets N52417's track at the C/J junction).
    /// </summary>
    [Fact]
    public void GiveWay_PastAClearedBarsHoldLine_ContinuesAcrossWithoutStoppingShortOfTheRunway()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, WithLeadOnJ());
        if (engine is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiingPhase", LogLevel.Debug).InitializeSimLog();
        AircraftState giver = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(giver.Ground.Layout);
        GroundNode firstBar = layout.Nodes[ClearedBarNodeId];
        var speeds = new List<(int Second, double SpeedKts)>();
        double approachDeg = double.NaN;
        int nearBarSegmentIndex = -1;
        TaxiRouteSegment? farBarSegment = null;
        bool enteredCrossing = false;
        int leadTaxiAt = -1;
        int giveWayAt = -1;
        int stoppedAt = KoakFollowClip.RunScript(
            engine,
            output,
            [.. Script.Where(line => (line.Second != GiveWaySecond) && (line.Second != LeadTaxiSecond))],
            LongTypeBudgetSeconds,
            second =>
            {
                if (leadTaxiAt < 0)
                {
                    if (GeoMath.DistanceNm(giver.Position, firstBar.Position) * GeoMath.FeetPerNm > LeadTaxiTriggerFt)
                    {
                        return false;
                    }

                    leadTaxiAt = second;
                    approachDeg = ApproachBearingDeg(firstBar, giver.Position);
                    TaxiRoute route = Assert.IsType<TaxiRoute>(giver.Ground.AssignedTaxiRoute);
                    nearBarSegmentIndex = route.Segments.FindIndex(s => s.ToNodeId == ClearedBarNodeId);
                    farBarSegment = FarBarSegment(route, ClearedBarNodeId);
                    Assert.True(engine.SendCommand(KoakFollowClip.Lead, LeadTaxiOnJ).Success);
                    return false;
                }

                double nosePastFt = NosePastHoldLineFt(Nose(giver), firstBar, approachDeg);
                double centrePastFt = NosePastHoldLineFt(giver.Position, firstBar, approachDeg);
                if (giveWayAt < 0)
                {
                    output.WriteLine(
                        $"t={second} gs={giver.GroundSpeed:F1} {giver.Phases?.CurrentPhase?.Name} nose {nosePastFt:F1} ft centre "
                            + $"{centrePastFt:F1} ft past the hold line | lead gs={lead.GroundSpeed:F1}"
                    );
                    if (
                        (nosePastFt <= 0.0)
                        || (centrePastFt >= 0.0)
                        || (giver.Phases?.CurrentPhase is not TaxiingPhase)
                        || (lead.GroundSpeed <= LeadRollingKts)
                        || (GroundConflictDetector.GiveWayStop(giver, lead, out _) is null)
                    )
                    {
                        Assert.True(centrePastFt < 0.0, "N738SP's centre reached the runway 33 hold line before the GIVEWAY could be issued");
                        return false;
                    }

                    giveWayAt = second;
                    output.WriteLine($"t={second}: GIVEWAY at gs={giver.GroundSpeed:F1}, nose {nosePastFt:F1} ft past the hold line");
                    Assert.True(engine.SendCommand(KoakFollowClip.Follower, $"GIVEWAY {KoakFollowClip.Lead}").Success);
                    speeds.Add((second, giver.GroundSpeed));
                    return false;
                }

                // A second under the ground-conflict speed limit is the conflict detector's slowdown, not the give-way's.
                if (giver.Ground.SpeedLimit is null)
                {
                    speeds.Add((second, giver.GroundSpeed));
                }

                enteredCrossing |=
                    (giver.Phases?.CurrentPhase is CrossingRunwayPhase)
                    || (giver.Ground.AssignedTaxiRoute?.CurrentSegmentIndex > nearBarSegmentIndex);
                output.WriteLine(
                    $"t={second} gs={giver.GroundSpeed:F1} centre {centrePastFt:F1} ft past the hold line hold={giver.Ground.Hold?.Kind} "
                        + $"{giver.Phases?.CurrentPhase?.Name} limit={giver.Ground.SpeedLimit?.ToString("F1")} | lead gs={lead.GroundSpeed:F1}"
                );
                return giver.GroundSpeed < StationaryKts;
            }
        );

        Assert.True(leadTaxiAt > 0, $"N738SP never came within {LeadTaxiTriggerFt:F0} ft of the runway 33 bar on C (node {ClearedBarNodeId})");
        Assert.True(giveWayAt > 0, "N738SP's nose never passed the runway 33 hold line with its centre short of it and N52417 rolling");
        Assert.True(enteredCrossing, $"N738SP never entered the runway 33 crossing past node {ClearedBarNodeId}");
        (double lossKts, int atSecond) = LargestLossOverConsecutiveSeconds(speeds);
        double firmLossKts = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        output.WriteLine($"largest one-second speed loss {lossKts:F2} kt ending t={atSecond}; firm rate allows {firmLossKts:F2}");
        Assert.True(lossKts <= firmLossKts + 1e-6, $"N738SP lost {lossKts:F2} kt in the second ending t={atSecond}, more than the firm rate allows");
        if (stoppedAt > 0)
        {
            GroundNode farBar = farBarSegment!.Edge.ToNode;
            double farApproachDeg = GeoMath.BearingTo(farBar.Position, farBarSegment.Edge.FromNode.Position);
            double nosePastNearFt = NosePastHoldLineFt(Nose(giver), firstBar, approachDeg);
            double tailPastFarFt = NosePastHoldLineFt(Tail(giver), farBar, farApproachDeg);
            output.WriteLine(
                $"at rest t={stoppedAt} ({giver.Phases?.CurrentPhase?.Name}): nose {nosePastNearFt:F1} ft past the near hold line, tail "
                    + $"{tailPastFarFt:F1} ft past the far marking (node {farBar.Id})"
            );
            Assert.True(
                (nosePastNearFt <= 0.0) || (tailPastFarFt >= 0.0),
                $"N738SP came to rest between the runway 33 holding-position markings: nose {nosePastNearFt:F1} ft past the near one, tail "
                    + $"{tailPastFarFt:F1} ft past the far one"
            );
        }
    }

    /// <summary>
    /// Characterizes a HOLD issued while N738SP is crossing runway 33 on C under its CROSS clearance: the crossing stops it
    /// where it is, on the runway, in the same second. A GIVEWAY waits for the far marking; a HOLD does not.
    /// </summary>
    [Fact]
    public void Hold_MidCrossing_StopsOnTheRunwayAtOnce()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, WithLeadOnJ());
        if (engine is null)
        {
            return;
        }

        AircraftState held = engine.FindAircraft(KoakFollowClip.Follower)!;
        int holdAt = -1;
        int stoppedAt = KoakFollowClip.RunScript(
            engine,
            output,
            [.. Script.Where(line => (line.Second != GiveWaySecond) && (line.Second != LeadTaxiSecond))],
            LongTypeBudgetSeconds,
            second =>
            {
                if (holdAt < 0)
                {
                    if ((held.Phases?.CurrentPhase is not CrossingRunwayPhase) || (held.GroundSpeed <= MovingKts))
                    {
                        return false;
                    }

                    holdAt = second;
                    output.WriteLine($"t={second}: HOLD mid-crossing at gs={held.GroundSpeed:F1}");
                    Assert.True(engine.SendCommand(KoakFollowClip.Follower, "HOLD").Success);
                    return false;
                }

                return true;
            }
        );

        Assert.True(holdAt > 0, "N738SP was never moving in its runway 33 crossing");
        output.WriteLine($"t={stoppedAt}: gs={held.GroundSpeed:F2} {held.Phases?.CurrentPhase?.Name} hold={held.Ground.Hold?.Kind}");
        Assert.IsType<CrossingRunwayPhase>(held.Phases?.CurrentPhase);
        Assert.True(held.GroundSpeed < StationaryKts, $"N738SP was still rolling at {held.GroundSpeed:F2} kt a second after HOLD mid-crossing");
    }

    /// <summary>The largest speed loss (kt) between two samples one second apart, and the second it ended at.</summary>
    private static (double LossKts, int AtSecond) LargestLossOverConsecutiveSeconds(List<(int Second, double SpeedKts)> speeds)
    {
        (double LossKts, int AtSecond) worst = (0.0, -1);
        for (int i = 1; i < speeds.Count; i++)
        {
            double loss = speeds[i - 1].SpeedKts - speeds[i].SpeedKts;
            if ((speeds[i].Second == speeds[i - 1].Second + 1) && (loss > worst.LossKts))
            {
                worst = (loss, speeds[i].Second);
            }
        }

        return worst;
    }

    /// <summary>
    /// The route segment ending at the exit-side bar of the runway the bar at <paramref name="nearNodeId"/> protects: the next
    /// node on the route carrying a hold-short for the same runway.
    /// </summary>
    private static TaxiRouteSegment FarBarSegment(TaxiRoute route, int nearNodeId)
    {
        var runway = RunwayIdentifier.Parse(Assert.IsType<string>(route.GetHoldShortAt(nearNodeId)?.TargetName));
        int nearIndex = route.Segments.FindIndex(s => s.ToNodeId == nearNodeId);
        TaxiRouteSegment? far = route
            .Segments.Skip(nearIndex + 1)
            .FirstOrDefault(s => (s.Edge.ToNode.Type == GroundNodeType.RunwayHoldShort) && (s.Edge.ToNode.RunwayId?.Equals(runway) == true));
        return Assert.IsType<TaxiRouteSegment>(far);
    }

    /// <summary>The aircraft's tail: its centre projected half its fuselage length behind along its heading.</summary>
    private static LatLon Tail(AircraftState aircraft) =>
        GeoMath.ProjectPoint(
            aircraft.Position,
            aircraft.TrueHeading.ToReciprocal(),
            AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm
        );

    /// <summary>
    /// The bearing from <paramref name="bar"/> back along the taxiway edge leading into it: of the bar node's non-runway
    /// edges, the one whose far end lies nearest in bearing to <paramref name="approachSide"/>.
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
    /// How far (ft) <paramref name="nose"/> is past the hold line through <paramref name="bar"/> — the line square to the
    /// approach edge (<paramref name="approachDeg"/> points back up it) — measured along that edge; negative when short.
    /// </summary>
    private static double NosePastHoldLineFt(LatLon nose, GroundNode bar, double approachDeg)
    {
        double distFt = GeoMath.DistanceNm(bar.Position, nose) * GeoMath.FeetPerNm;
        double offRad = GeoMath.SignedBearingDifference(approachDeg, GeoMath.BearingTo(bar.Position, nose)) * Math.PI / 180.0;
        return -(distFt * Math.Cos(offRad));
    }

    /// <summary>The runway 33 bar on C that N738SP, eastbound on C, meets first (KOAK layout node).</summary>
    private const int ClearedBarNodeId = 518;

    /// <summary>How close (ft) N738SP comes to that bar before N52417 is told to taxi: both then reach the C/J junction together.</summary>
    private const double LeadTaxiTriggerFt = 250.0;

    private const string LeadTaxiOnJ = "TAXI J RWY 28R";

    /// <summary>How fast (kt) N52417 must be rolling before the GIVEWAY: a GIVEWAY to traffic standing still releases at once.</summary>
    private const double LeadRollingKts = 5.0;

    /// <summary>
    /// The H2 scenario with N52417 standing on taxiway J north-east of C (KOAK node 376), facing the C/J junction (node 352).
    /// </summary>
    private static string WithLeadOnJ()
    {
        JsonNode scenario = Assert.IsAssignableFrom<JsonNode>(JsonNode.Parse(Scenario));
        JsonNode lead = Assert.Single(scenario["aircraft"]!.AsArray(), a => (string?)a?["aircraftId"] == KoakFollowClip.Lead)!;
        JsonNode conditions = lead["startingConditions"]!;
        conditions["coordinates"]!["lat"] = 37.730627;
        conditions["coordinates"]!["lon"] = -122.216111;
        conditions["heading"] = 254;
        return scenario.ToJsonString();
    }

    /// <summary>
    /// The first node ahead on <paramref name="aircraft"/>'s route carrying a hold-short, when it comes before
    /// <paramref name="junctionNodeId"/>.
    /// </summary>
    private static int? HoldShortNodeBefore(AircraftState aircraft, int junctionNodeId)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        foreach (TaxiRouteSegment segment in route.Segments.Skip(Math.Max(route.CurrentSegmentIndex, 0)))
        {
            if (segment.ToNodeId == junctionNodeId)
            {
                return null;
            }

            if (route.GetHoldShortAt(segment.ToNodeId) is not null)
            {
                return segment.ToNodeId;
            }
        }

        return null;
    }

    /// <summary>Once N52417 has crossed and the hold releases, N738SP taxis on along its route.</summary>
    [Fact]
    public void GiveWay_ResumesTaxiAfterTargetPasses()
    {
        SimulationEngine? engine = KoakFollowClip.Load(output, Scenario);
        if (engine is null)
        {
            return;
        }

        AircraftState giver = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        bool stopped = false;
        int resumedAt = KoakFollowClip.RunScript(
            engine,
            output,
            Script,
            GiveWaySecond + ResumeBudgetSeconds,
            second =>
            {
                stopped |= (second > GiveWaySecond) && (giver.GroundSpeed < StationaryKts);
                if ((second >= GiveWaySecond) && (second % 3 == 0))
                {
                    output.WriteLine(
                        $"t={second} giver gs={giver.GroundSpeed:F1} hold={giver.Ground.Hold?.Kind} | lead gs={lead.GroundSpeed:F1} "
                            + $"limit={lead.Ground.SpeedLimit?.ToString("F1")} seg={lead.Ground.AssignedTaxiRoute?.CurrentSegmentIndex} "
                            + $"sep={GeoMath.DistanceNm(giver.Position, lead.Position) * GeoMath.FeetPerNm:F0} ft"
                    );
                }

                return stopped && (giver.Ground.Hold is null) && (giver.GroundSpeed > MovingKts);
            }
        );

        Assert.True(stopped, "N738SP never stopped for its give-way");
        Assert.True(resumedAt > 0, $"N738SP never resumed its taxi within {ResumeBudgetSeconds}s of GIVEWAY: hold={giver.Ground.Hold?.Kind}");
        Assert.IsType<TaxiingPhase>(giver.Phases?.CurrentPhase);
        output.WriteLine($"resumed at t={resumedAt} on {giver.Ground.CurrentTaxiway}");
    }

    private static void AssertNoFasterThanTheTaxiBrakeRate(double lossKts, int atSecond) =>
        Assert.True(
            lossKts <= KoakFollowClip.MaxPistonSpeedLossPerSecondKts + 1e-6,
            $"N738SP lost {lossKts:F2} kt in the second ending t={atSecond}; a piston brakes at most "
                + $"{KoakFollowClip.MaxPistonSpeedLossPerSecondKts:F2} kt/s (taxi brake rate plus one sub-tick of snap)"
        );

    /// <summary>The room two C172s need to pass: both half-spans plus the wingtip margin.</summary>
    private static double RequiredClearanceFt() =>
        Assert.IsType<double>(FaaAircraftDatabase.Get("C172")?.WingspanFt) + GroundOutlineSweep.WingtipBufferFt;

    private void LogStop(AircraftState giver, JunctionTrack junction, double clearanceFt, double requiredFt, int second)
    {
        double straightFt = GeoMath.DistanceNm(giver.Position, junction.Node.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"at rest t={second}: centre {clearanceFt:F1} ft off N52417's track (needs {requiredFt:F1}), {straightFt:F1} ft from the "
                + $"F/K junction (node {junction.Node.Id})"
        );
    }

    /// <summary>
    /// The F/K/L junction on N738SP's route ahead, and N52417's track through it — its route's edges into and out of the
    /// node — captured while both routes still run there.
    /// </summary>
    private sealed record JunctionTrack(GroundNode Node, List<DirectionalEdge> Track, int LeadSegmentIntoNode)
    {
        internal static JunctionTrack Capture(AircraftState giver, AircraftState lead)
        {
            TaxiRoute giverRoute = Assert.IsType<TaxiRoute>(giver.Ground.AssignedTaxiRoute);
            TaxiRouteSegment intoJunction = Assert.Single(
                giverRoute.Segments.Skip(Math.Max(giverRoute.CurrentSegmentIndex, 0)),
                s => s.Edge.ToNode.Edges.Any(e => e.TaxiwayName == "F") && s.Edge.ToNode.Edges.Any(e => e.TaxiwayName == "K")
            );
            GroundNode node = intoJunction.Edge.ToNode;
            TaxiRoute leadRoute = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
            int leadInto = leadRoute.Segments.FindIndex(Math.Max(leadRoute.CurrentSegmentIndex, 0), s => s.ToNodeId == node.Id);
            Assert.True(leadInto >= 0, $"N52417's route does not run through the F/K junction (node {node.Id})");
            List<DirectionalEdge> track = [leadRoute.Segments[leadInto].Edge];
            if (leadInto + 1 < leadRoute.Segments.Count)
            {
                track.Add(leadRoute.Segments[leadInto + 1].Edge);
            }

            return new JunctionTrack(node, track, leadInto);
        }

        /// <summary>N52417's route cursor has moved past the segment that ends at the junction.</summary>
        internal bool LeadIsPast(AircraftState lead) =>
            (lead.Ground.AssignedTaxiRoute is not { } route) || (route.CurrentSegmentIndex > LeadSegmentIntoNode);

        /// <summary>
        /// How far (ft) <paramref name="point"/> is from N52417's track, measured here rather than by the detector: the
        /// shortest distance to the polyline through each track edge's pavement — a straight edge's end nodes and intermediate
        /// points, a fillet's curve sampled every 1/<see cref="ArcSamples"/> of its parameter.
        /// </summary>
        internal double ClearanceFt(LatLon point) =>
            Track
                .SelectMany(edge => Polyline(edge.Edge).Zip(Polyline(edge.Edge).Skip(1)))
                .Min(piece => GeoMath.DistanceToSegmentFt(point, piece.First, piece.Second));

        private static List<LatLon> Polyline(IGroundEdge edge)
        {
            if (edge is GroundArc arc)
            {
                CubicBezier curve = arc.ToBezier();
                return
                [
                    .. Enumerable.Range(0, ArcSamples + 1).Select(i => curve.Evaluate((double)i / ArcSamples)).Select(p => new LatLon(p.Lat, p.Lon)),
                ];
            }

            GroundEdge straight = Assert.IsType<GroundEdge>(edge);
            return [straight.Nodes[0].Position, .. straight.IntermediatePoints.Select(p => new LatLon(p.Lat, p.Lon)), straight.Nodes[1].Position];
        }
    }
}

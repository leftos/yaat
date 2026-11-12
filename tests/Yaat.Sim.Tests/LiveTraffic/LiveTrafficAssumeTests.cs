using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// <c>ASSUME</c> converts a shadow in place into a controllable aircraft. It is never refused; the
/// seeded state depends on the situation (feed clearance fields first, then level/climb/descent
/// inference, established-on-final, route rejoin, hold, VFR, runway/surface kinds).
/// </summary>
public class LiveTrafficAssumeTests
{
    private const string Callsign = "UAL123";
    private static readonly LatLon EnRoute = new(37.9, -121.3);

    public LiveTrafficAssumeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LiveTrafficSample Sample(
        double at,
        LatLon pos,
        double altFt,
        double gs,
        double track,
        double? vs,
        LiveTrafficSource source = LiveTrafficSource.Stars
    ) => new(at, pos.Lat, pos.Lon, altFt, gs, track, vs, source, 4521);

    private static AircraftState Shadow(LiveTrafficSample sample, AircraftFlightPlan? plan = null) =>
        LiveTrafficKinematics.CreateShadow(
            Callsign,
            "B738",
            sample,
            plan
                ?? new AircraftFlightPlan
                {
                    HasFlightPlan = true,
                    Departure = "KSFO",
                    Destination = "KSMF",
                }
        );

    private static DispatchContext Ctx(AircraftState ac) =>
        TestDispatch.Context(new Random(1), findAircraft: cs => cs == Callsign ? ac : null, listAircraft: () => [ac]);

    private static CommandResult Assume(AircraftState ac) => CommandDispatcher.Dispatch(new AssumeCommand(), ac, Ctx(ac));

    private static CommandResult Send(AircraftState ac, string input)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx(ac));
    }

    [Fact]
    public void Assume_ClearsShadowState_AndCommandsWorkAfterwards()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsShadow);
        Assert.Contains("assumed", result.Message, StringComparison.Ordinal);
        Assert.Equal(4521u, ac.Transponder.Code);
        Assert.True(Send(ac, "H 180").Success);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void Assume_ClearsThePilotToTheFeedPlansDestination()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        Assert.Equal("", ac.ClearedDestination);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSMF", ac.ClearedDestination);
    }

    [Fact]
    public void Assume_OnNonShadow_IsRejected()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        Assume(ac);

        CommandResult again = Assume(ac);

        Assert.False(again.Success);
        Assert.Contains("not live traffic", again.Message, StringComparison.Ordinal);
    }

    /// <summary>An engine whose world holds <paramref name="ac"/>, for the verbs the action router owns.</summary>
    private static SimulationEngine EngineWith(AircraftState ac)
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.AddAircraft(ac);
        return engine;
    }

    private static CommandResult Route(SimulationEngine engine, string command) =>
        engine.Actions.Issue(new ActionInput(Callsign, command, "conn-1", "XX", Baked: null)).Result;

    [Fact]
    public void Unassume_OnNeverAssumed_IsRejected()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        ac.LiveTraffic = null;
        SimulationEngine engine = EngineWith(ac);

        CommandResult result = Route(engine, "UNASSUME");

        Assert.False(result.Success);
        Assert.Equal($"{Callsign} was not assumed from live traffic", result.Message);
        Assert.NotNull(engine.World.FindAircraft(Callsign));
    }

    [Fact]
    public void Unassume_OnStillShadow_IsRejected()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        SimulationEngine engine = EngineWith(ac);

        CommandResult result = Route(engine, "UNASSUME");

        Assert.False(result.Success);
        Assert.Equal($"{Callsign} is live traffic already", result.Message);
        Assert.True(engine.World.FindAircraft(Callsign)!.IsShadow);
    }

    /// <summary>
    /// The aircraft leaves the world the way a <c>DEL</c> removes one, but nothing tells the feed to stop: no
    /// <see cref="RecordedLiveTrafficRemoval"/> is written, so the next sync re-spawns the shadow from the store.
    /// The shadow here carries no feed identity — a track no product gave a GUFI — and is still handed back, because
    /// the marker is a flag rather than that identity.
    /// </summary>
    [Fact]
    public void Unassume_AfterAssume_RemovesTheAircraft_AndRecordsNoLiveTrafficRemoval()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        Assert.Null(ac.LiveTraffic!.ExternalId);
        SimulationEngine engine = EngineWith(ac);
        Assert.True(Route(engine, "ASSUME").Success);
        Assert.True(ac.AssumedFromLiveTraffic);

        CommandResult result = Route(engine, "UNASSUME");

        Assert.True(result.Success, result.Message);
        Assert.Null(engine.World.FindAircraft(Callsign));
        Assert.Empty(engine.Scenario!.ActionLog.OfType<RecordedLiveTrafficRemoval>());
        RecordedCommand record = Assert.Single(engine.Scenario.ActionLog.OfType<RecordedCommand>(), r => r.Command == "UNASSUME");
        Assert.True(record.Accepted);
        Assert.Equal("UNASSUME", Assert.Single(engine.World.GetCompletedAircraft()).Detail);
    }

    /// <summary>
    /// A chain carrying any rejection-set verb (<see cref="CompoundPolicy.IsNonCompoundable"/>) is refused before
    /// anything happens, so the aircraft is still live traffic afterwards — the gate must not take a real aircraft for
    /// a chain that is going to fail on the missing arm anyway. Pinned on both paths that can dispatch one: the
    /// router's pre-check, and the dispatcher's own guard, which is what catches the spaced form (the pre-check reads
    /// <c>ASSUME ; H 180</c> as one unsupported command) and any direct dispatch — a deferred payload, a
    /// reconstruction — that never passes the pre-check at all.
    /// </summary>
    [Theory]
    [InlineData("ASSUME ; H 180", "ASSUME")]
    [InlineData("PAUSE ; H 180", "PAUSE")]
    [InlineData("SPAWN ; H 180", "SPAWN")]
    public void AChainedNonCompoundable_IsRefused_BeforeTheAircraftIsTaken(string command, string verb)
    {
        AircraftState viaDispatcher = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));

        CommandResult dispatched = Send(viaDispatcher, command);

        Assert.False(dispatched.Success);
        Assert.Equal($"{verb} cannot be part of a chained command", dispatched.Message);
        Assert.True(viaDispatcher.IsShadow);
        Assert.Null(viaDispatcher.Targets.AssignedMagneticHeading);

        AircraftState viaRouter = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        CommandResult routed = Route(EngineWith(viaRouter), command);

        Assert.False(routed.Success);
        Assert.Equal(dispatched.Message, routed.Message);
        Assert.True(viaRouter.IsShadow);
    }

    /// <summary>The way back is a lone verb too: <c>H 070; UNASSUME</c> never reaches the arm that would remove it.</summary>
    [Fact]
    public void UnassumeInsideACompound_IsRefused()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        SimulationEngine engine = EngineWith(ac);
        Assert.True(Route(engine, "ASSUME").Success);

        CommandResult result = Route(engine, "H 070; UNASSUME");

        Assert.False(result.Success);
        Assert.Equal("UNASSUME cannot be part of a chained command", result.Message);
        Assert.NotNull(engine.World.FindAircraft(Callsign));
        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }

    private static string Json<T>(T dto) => JsonSerializer.Serialize(dto);

    /// <summary>
    /// The automatic assume is undone when the command it took the aircraft for is refused: the seeded state goes
    /// back the way it was, the satellite is the same object, and the refusal reads as the plain refusal it is —
    /// no "assumed —" prefix for a hand-off that did not stick.
    /// </summary>
    [Fact]
    public void AutoAssume_RefusedCommand_RollsBackToShadow()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        AircraftLiveTraffic? satellite = ac.LiveTraffic;
        string targets = Json(ac.Targets.ToSnapshot());
        string procedure = Json(ac.Procedure.ToSnapshot());
        string approach = Json(ac.Approach.ToSnapshot());
        int warnings = ac.PendingWarnings.Count;

        // A ground verb on an airborne aircraft: refused by the seeded state, deterministically.
        CommandResult result = Send(ac, "TAXI A");

        Assert.False(result.Success);
        Assert.Contains("requires the aircraft to be on the ground", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("assumed", result.Message, StringComparison.Ordinal);
        Assert.True(ac.IsShadow);
        Assert.False(ac.AssumedFromLiveTraffic);
        Assert.Same(satellite, ac.LiveTraffic);
        Assert.Null(ac.Phases);
        Assert.Equal(targets, Json(ac.Targets.ToSnapshot()));
        Assert.Equal(procedure, Json(ac.Procedure.ToSnapshot()));
        Assert.Equal(approach, Json(ac.Approach.ToSnapshot()));
        Assert.Equal(warnings, ac.PendingWarnings.Count);
    }

    /// <summary>
    /// The same rollback on the seed's heaviest path: established on a final installs an approach, which is where the
    /// phase list is cleared in place and where a first phase's <c>OnStart</c> can queue a pilot transmission. Nothing
    /// the seed touched survives the refusal.
    /// </summary>
    [Fact]
    public void AutoAssume_RefusedCommand_OnTheFinal_RollsBackTheApproachSeed()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(runway);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), 3.0);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt + 950, 140, runway.TrueHeading.Degrees, -700), plan);
        AircraftLiveTraffic? satellite = ac.LiveTraffic;
        string targets = Json(ac.Targets.ToSnapshot());
        string procedure = Json(ac.Procedure.ToSnapshot());
        string approach = Json(ac.Approach.ToSnapshot());
        var observations = ac.PendingObservations.ToList();
        int warnings = ac.PendingWarnings.Count;
        int transmissions = ac.PendingPilotTransmissions.Count;
        int speech = ac.PendingPilotSpeech.Count;

        CommandResult result = Send(ac, "TAXI A");

        Assert.False(result.Success);
        Assert.DoesNotContain("assumed", result.Message, StringComparison.Ordinal);
        Assert.True(ac.IsShadow);
        Assert.False(ac.AssumedFromLiveTraffic);
        Assert.Same(satellite, ac.LiveTraffic);
        Assert.Null(ac.Phases);
        Assert.Equal(targets, Json(ac.Targets.ToSnapshot()));
        Assert.Equal(procedure, Json(ac.Procedure.ToSnapshot()));
        Assert.Equal(approach, Json(ac.Approach.ToSnapshot()));
        Assert.Equal(observations, ac.PendingObservations);
        Assert.Equal(warnings, ac.PendingWarnings.Count);
        Assert.Equal(transmissions, ac.PendingPilotTransmissions.Count);
        Assert.Equal(speech, ac.PendingPilotSpeech.Count);

        // The seed really does install an approach on this fixture, so the rollback above undid something.
        CommandResult assumed = Assume(ac);
        Assert.True(assumed.Success, assumed.Message);
        Assert.NotNull(ac.Phases?.ActiveApproach);
    }

    /// <summary>The other side of the rollback branch: a command the seeded state accepts keeps the aircraft.</summary>
    [Fact]
    public void AutoAssume_AcceptedCommand_StaysAssumed()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));

        CommandResult result = Send(ac, "FH 070");

        Assert.True(result.Success, result.Message);
        Assert.StartsWith($"{Callsign} assumed — ", result.Message, StringComparison.Ordinal);
        Assert.False(ac.IsShadow);
        Assert.True(ac.AssumedFromLiveTraffic);
        Assert.Null(ac.LiveTraffic);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void LevelAircraft_KeepsItsAltitudeToTheHundred_NotAHemisphericSnap()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 270, 0));
        LiveTrafficKinematics.Apply(ac, Sample(5, EnRoute, 11_100, 300, 270, null));
        LiveTrafficKinematics.Apply(ac, Sample(10, EnRoute, 11_000, 300, 270, null));

        Assume(ac);

        Assert.Equal(11_000, ac.Targets.TargetAltitude);
        Assert.Equal(11_000, ac.Targets.AssignedAltitude);
        Assert.InRange(ac.Targets.TargetSpeed!.Value, 200, 340);
    }

    [Fact]
    public void Descending_TargetsTheFeedsAssignedAltitude_AndKeepsTheRate()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 12_000, 280, 90, -1_800) with { AssignedAltitudeFt = 6_000 });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(6_000, ac.Targets.TargetAltitude);
        Assert.Equal(1_800, ac.Targets.DesiredVerticalRate);
        Assert.Contains("descending to 6000", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Descending_InterimAltitudeBeatsAssigned()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 12_000, 280, 90, -1_500) with { AssignedAltitudeFt = 4_000, InterimAltitudeFt = 8_000 });

        Assume(ac);

        Assert.Equal(8_000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void Descending_WithoutAClearance_HoldsTheDescentToAFloor_NotTheNextThousand()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 12_000, 280, 90, -2_000));

        Assume(ac);

        double target = ac.Targets.TargetAltitude!.Value;
        Assert.True(target < 12_000, $"target {target} should be below the aircraft");
        Assert.True(target <= 5_000, $"floor {target} should be near the destination, not the next hemispheric altitude");
        Assert.Equal(2_000, ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void Climbing_TargetsTheFiledAltitude_WhenTheFeedHasNone()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KSFO",
            Destination = "KSMF",
            Altitude = new PlannedAltitude(17_000, null, false, false, false),
        };
        AircraftState ac = Shadow(Sample(0, EnRoute, 9_000, 280, 90, 2_200), plan);

        Assume(ac);

        Assert.Equal(17_000, ac.Targets.TargetAltitude);
        Assert.Equal(2_200, ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void HighAltitude_SeedsMachFromTheAirVector()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 35_000, 450, 90, 0, LiveTrafficSource.Eram));

        Assume(ac);

        Assert.NotNull(ac.Targets.TargetMach);
        Assert.InRange(ac.Targets.TargetMach!.Value, 0.70, 0.86);
    }

    [Fact]
    public void ClearedHeadingFromTheFeed_SeedsTheHeadingHold()
    {
        AircraftState ac = Shadow(
            Sample(0, EnRoute, 11_000, 300, 90, 0) with
            {
                ClearedHeadingDeg = 120,
            },
            new AircraftFlightPlan { HasFlightPlan = true }
        );

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        Assert.InRange(ac.Targets.TargetTrueHeading!.Value.ToMagnetic(ac.Declination).Degrees, 119.5, 120.5);
    }

    [Fact]
    public void NoRoute_HoldsThePresentHeading_WithAVectorsNote()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0), new AircraftFlightPlan { HasFlightPlan = true });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.InRange(ac.Targets.TargetTrueHeading!.Value.Degrees, 89, 91);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("vectors", StringComparison.Ordinal));
    }

    [Fact]
    public void FiledRoute_RejoinsAtTheNextFixAhead_NotOneBehind()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon)? oak = navDb.ResolveFixOrFrd("OAK");
        (double Lat, double Lon)? sac = navDb.ResolveFixOrFrd("SAC");
        (double Lat, double Lon)? rbl = navDb.ResolveFixOrFrd("RBL");
        Assert.NotNull(oak);
        Assert.NotNull(sac);
        Assert.NotNull(rbl);

        var a = new LatLon(oak.Value.Lat, oak.Value.Lon);
        var b = new LatLon(sac.Value.Lat, sac.Value.Lon);
        double legNm = GeoMath.DistanceNm(a, b);
        var bearing = new TrueHeading(GeoMath.BearingTo(a, b));
        LatLon pos = GeoMath.ProjectPoint(a, bearing, legNm * 0.4);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        AircraftState ac = Shadow(Sample(0, pos, 11_000, 300, bearing.Degrees, 0), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["SAC", "RBL"], ac.Targets.NavigationRoute.Select(t => t.Name).ToList());
        Assert.Contains("direct SAC", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NextFixAhead_SkipsAFixAbeam_ButNeverTwo()
    {
        var a = new LatLon(37.0, -122.0);
        LatLon b = GeoMath.ProjectPoint(a, new TrueHeading(90), 10);
        LatLon c = GeoMath.ProjectPoint(a, new TrueHeading(90), 20);
        LatLon d = GeoMath.ProjectPoint(a, new TrueHeading(90), 30);
        List<ResolvedFix> route = [new("A", a.Lat, a.Lon), new("B", b.Lat, b.Lon), new("C", c.Lat, c.Lon), new("D", d.Lat, d.Lon)];
        LatLon abeamB = GeoMath.ProjectPoint(b, new TrueHeading(0), 0.6);
        var ac = new AircraftState
        {
            Callsign = "N1",
            AircraftType = "B738",
            Position = abeamB,
            TrueTrack = new TrueHeading(90),
            TrueHeading = new TrueHeading(90),
            IndicatedAirspeed = 250,
            Altitude = 10_000,
        };

        // Abeam B (bearing 180 = behind) → skip once → C.
        Assert.Equal(2, LiveTrafficAssumer.NextFixAhead(ac, route));

        // Past the last fix of a two-fix route: nothing ahead, never turn back.
        ac.Position = GeoMath.ProjectPoint(b, new TrueHeading(90), 1);
        Assert.Equal(-1, LiveTrafficAssumer.NextFixAhead(ac, [.. route.Take(2)]));
    }

    [Fact]
    public void FiledRoute_OutsideTheRejoinCone_FallsBackToVectors()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon) oak = navDb.ResolveFixOrFrd("OAK")!.Value;
        (double Lat, double Lon) sac = navDb.ResolveFixOrFrd("SAC")!.Value;
        var a = new LatLon(oak.Lat, oak.Lon);
        var b = new LatLon(sac.Lat, sac.Lon);
        var bearing = new TrueHeading(GeoMath.BearingTo(a, b));
        LatLon pos = GeoMath.ProjectPoint(a, bearing, 20);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        // Tracking 90° off the leg: no fix inside the ±45° cone.
        AircraftState ac = Shadow(Sample(0, pos, 11_000, 300, bearing.Degrees + 90, 0), plan);

        Assume(ac);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void VfrShadow_MaintainsVfr_NoRouteNoSnap()
    {
        var plan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" };
        AircraftState ac = Shadow(Sample(0, EnRoute, 4_500, 110, 90, 0) with { BeaconCode = 1200 }, plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("VFR", result.Message, StringComparison.Ordinal);
        Assert.Equal(4_500, ac.Targets.TargetAltitude);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void AirborneHoldFromTheFeed_HoldsHeadingAndAltitude_AndNamesTheFix()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        (double Lat, double Lon) sac = NavigationDatabase.Instance.ResolveFixOrFrd("SAC")!.Value;
        AircraftState ac = Shadow(
            Sample(0, new LatLon(sac.Lat + 0.05, sac.Lon), 8_000, 210, 180, 0) with
            {
                AirborneHold = true,
                HoldFix = "SAC",
            },
            plan
        );

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(8_000, ac.Targets.TargetAltitude);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("hold at SAC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HoldInClearanceText_HoldsHeadingAndAltitude_NeverRejoins()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon) sac = navDb.ResolveFixOrFrd("SAC")!.Value;
        AircraftState ac = Shadow(
            Sample(0, new LatLon(sac.Lat + 0.05, sac.Lon), 8_000, 210, 180, 0) with
            {
                ClearanceText = "HOLD SAC AS PUBLISHED EFC 1230",
            },
            plan
        );

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(8_000, ac.Targets.TargetAltitude);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("hold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RacetrackSignature_DetectsAHold()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon) sac = navDb.ResolveFixOrFrd("SAC")!.Value;
        var center = new LatLon(sac.Lat, sac.Lon);
        AircraftState ac = Shadow(Sample(0, center, 8_000, 210, 0, 0), plan);
        // Standard-rate turn (3°/s, AIM 5-3-8) for 72 s within a mile of the fix.
        for (int i = 1; i <= 16; i++)
        {
            double track = (i * 13.5) % 360;
            LatLon pos = GeoMath.ProjectPoint(center, new TrueHeading(track + 90), 0.8);
            LiveTrafficKinematics.Apply(ac, Sample(i * 4.5, pos, 8_000, 210, track, null));
        }

        Assume(ac);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("hold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EstablishedOnFinal_ContinuesTheApproach_AndGoAroundWorks()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(runway);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), 3.0);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt + 950, 140, runway.TrueHeading.Degrees, -700), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Phases);
        Assert.NotNull(ac.Phases.ActiveApproach);
        Assert.Equal("28R", RunwayIdentifier.ToDisplayDesignator(ac.Phases.AssignedRunway!.Designator));
        Assert.Null(ac.Phases.LandingClearance);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("no landing clearance", StringComparison.Ordinal));

        CommandResult ga = Send(ac, "GA");
        Assert.True(ga.Success, ga.Message);
    }

    [Fact]
    public void AlignedButOutsideTheGate_IsOnVectorsTowardTheRunway()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), 9.0);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        AircraftState ac = Shadow(Sample(0, pos, 3_000, 180, runway.TrueHeading.Degrees, -500), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("vectors to the runway 28R", StringComparison.Ordinal));
    }

    [Fact]
    public void HelicopterDescendingOntoTheRunway_LandsUnderHelicopterLandingPhase()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.2);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        AircraftState ac = LiveTrafficKinematics.CreateShadow(
            Callsign,
            "EC35",
            Sample(0, pos, runway.ElevationFt + 40, 15, runway.TrueHeading.Degrees, -200),
            plan
        );

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterLandingPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
    }

    [Fact]
    public void AirborneOverTheRunwayBelow50Ft_LandsUnderLandingPhase_ClearanceImplied()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.2);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt + 30, 130, runway.TrueHeading.Degrees, -300), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.IsType<LandingPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
    }

    [Fact]
    public void RollingDeparture_KeepsRollingUnderTakeoffPhase()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.3);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KLAX",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt, 80, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.IsType<TakeoffPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(runway.Designator, ac.Procedure.DepartureRunway);
    }

    [Fact]
    public void RolloutAbove30Kt_ExitsUnderRunwayExitPhase()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.7);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt, 20, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex), plan);
        LiveTrafficKinematics.Apply(ac, Sample(1, pos, runway.ElevationFt, 34, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex));

        Assume(ac);

        Assert.IsType<RunwayExitPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void SlowSurfaceTarget_BecomesAPhaselessGroundAircraft()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.7);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt, 12, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsOnGround);
        Assert.Null(ac.Phases);
        Assert.Equal(12, ac.Targets.TargetSpeed);
        Assert.False(ac.IsShadow);
    }

    [Fact]
    public void Descending_NothingToDescendTo_LevelsOffWithANote()
    {
        // Mid-Pacific: no MVA coverage, no resolvable destination.
        AircraftState ac = Shadow(
            Sample(0, new LatLon(30.0, -150.0), 12_000, 280, 90, -2_000),
            new AircraftFlightPlan { HasFlightPlan = true, Destination = "ZZZZ" }
        );

        Assume(ac);

        Assert.Equal(12_000, ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.DesiredVerticalRate);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("levelled off", StringComparison.Ordinal));
    }

    [Fact]
    public void Descending_JustAboveAHundred_NeverTargetsAboveItself()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 956, 120, 90, -500) with { AssignedAltitudeFt = 3_000 });

        Assume(ac);

        Assert.True(ac.Targets.TargetAltitude <= 956, $"target {ac.Targets.TargetAltitude} above the aircraft");
    }

    [Fact]
    public void SlowArrivalBelow10k_IsNotSpedBackUp()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 2_000, 150, 90, -700));

        Assume(ac);

        Assert.InRange(ac.Targets.TargetSpeed!.Value, 120, 160);
    }

    [Fact]
    public void ClimbingVfr_KeepsClimbingToTheNextVfrCruisingAltitude()
    {
        var plan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" };
        AircraftState ac = Shadow(Sample(0, EnRoute, 4_200, 110, 90, 700) with { BeaconCode = 1200 }, plan);

        Assume(ac);

        Assert.Equal(700, ac.Targets.DesiredVerticalRate);
        Assert.Equal(5_500, ac.Targets.TargetAltitude);
    }

    [Theory]
    [InlineData(4_200, 90, true, 5_500)]
    [InlineData(4_200, 270, true, 4_500)]
    [InlineData(6_600, 90, false, 5_500)]
    [InlineData(6_600, 270, false, 6_500)]
    public void NextVfrCruisingAltitude_FollowsTheHemisphericRule(double alt, double course, bool up, double expected) =>
        Assert.Equal(expected, LiveTrafficAssumer.NextVfrCruisingAltitude(alt, course, up));

    [Fact]
    public void InitialClimb_HoldsRunwayHeading_NotDirectToTheFirstFix()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var der = new LatLon(runway.EndLatitude, runway.EndLongitude);
        LatLon pos = GeoMath.ProjectPoint(der, runway.TrueHeading, 1.5);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "SAC RBL",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt + 900, 160, runway.TrueHeading.Degrees, 2_500), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("initial climb runway 28R", result.Message, StringComparison.Ordinal);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.InRange(Math.Abs(ac.Targets.TargetTrueHeading!.Value - runway.TrueHeading), 0, 0.5);
    }

    /// <summary>
    /// An assumed departure in its initial climb on a filed SID gets a climb via, the path a TDLS-put climb via takes, so
    /// the SID's published altitudes drive it and the summary's "restrictions active" is true; the observed climb target
    /// does not linger as an assigned altitude, as after a CVIA.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_GetsClimbVia_PublishedAltitudesDrive()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        var der = new LatLon(runway.EndLatitude, runway.EndLongitude);
        LatLon pos = GeoMath.ProjectPoint(der, runway.TrueHeading, 1.5);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KLAS",
            Route = "CNDEL5 SUSEY",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt + 900, 160, runway.TrueHeading.Degrees, 2_500), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("restrictions active", result.Message, StringComparison.Ordinal);
        Assert.NotNull(ac.Procedure.ActiveSidId);
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        Assert.True(AltitudeRestrictionInForce.ProcedureAltitudesInForce(ac));
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.Equal(FlightPhysics.ClimbViaAltitude(ac), ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// An assumed departure on CNDEL5, a SID with published crossing restrictions (LEJAY and PORTE at or below), whose feed
    /// carries a controller's interim altitude climbs via the SID except maintain that altitude: the climb via's top and its
    /// "except maintain" are the interim, and as after a CVIA the aircraft carries no assigned altitude.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_WithFeedInterim_ClimbsViaExceptMaintain()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        var der = new LatLon(runway.EndLatitude, runway.EndLongitude);
        LatLon pos = GeoMath.ProjectPoint(der, runway.TrueHeading, 1.5);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KLAS",
            Route = "CNDEL5 SUSEY",
        };
        AircraftState ac = Shadow(
            Sample(0, pos, runway.ElevationFt + 900, 160, runway.TrueHeading.Degrees, 2_500) with
            {
                InterimAltitudeFt = 5_000,
            },
            plan
        );

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("restrictions active, maintain 5000", result.Message, StringComparison.Ordinal);
        Assert.NotNull(ac.Procedure.ActiveSidId);
        Assert.Equal(5_000, ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(5_000, ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.True(AltitudeRestrictionInForce.ProcedureAltitudesInForce(ac));
    }

    /// <summary>An assumed departure in its initial climb with no SID in its filed route gets no climb via.</summary>
    [Fact]
    public void AssumedNonSidDeparture_NoClimbVia()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var der = new LatLon(runway.EndLatitude, runway.EndLongitude);
        LatLon pos = GeoMath.ProjectPoint(der, runway.TrueHeading, 1.5);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "SAC RBL",
        };
        AircraftState ac = Shadow(Sample(0, pos, runway.ElevationFt + 900, 160, runway.TrueHeading.Degrees, 2_500), plan);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("initial climb runway 28R", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("restrictions active", result.Message, StringComparison.Ordinal);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// An assumed CNDEL5 departure whose feed's assigned altitude equals the filed cruise carries no amendment, so it gets the
    /// climb via as a departure with no feed altitude does.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_AssignedEqualsCruise_GetsClimbVia()
    {
        AircraftState ac = Koak30InitialClimbShadow("CNDEL5 SUSEY", "KLAS", s => s with { AssignedAltitudeFt = 35_000 });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("restrictions active", result.Message, StringComparison.Ordinal);
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.True(AltitudeRestrictionInForce.ProcedureAltitudesInForce(ac));
    }

    /// <summary>
    /// An assumed CNDEL5 departure whose feed carries only an assigned altitude below the filed cruise keeps it as a plain
    /// maintain: the SID is active with its published altitudes out of force, and the summary names the SID and the altitude.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_AssignedBelowCruise_KeepsItNoClimbVia()
    {
        AircraftState ac = Koak30InitialClimbShadow("CNDEL5 SUSEY", "KLAS", s => s with { AssignedAltitudeFt = 12_000 });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("restrictions active", result.Message, StringComparison.Ordinal);
        Assert.NotNull(ac.Procedure.ActiveSidId);
        Assert.Contains($"{ac.Procedure.ActiveSidId} active, maintain 12000", result.Message, StringComparison.Ordinal);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(12_000, ac.Targets.AssignedAltitude);
        Assert.Equal(12_000, ac.Targets.TargetAltitude);
        Assert.False(AltitudeRestrictionInForce.ProcedureAltitudesInForce(ac));
    }

    /// <summary>
    /// An assumed CNDEL5 departure rejoining at PORTE (at or below 10,000) with a feed interim of 12,000 climbs via the SID
    /// except maintain 12,000: the via's top and "except maintain" are the interim, it carries no assigned altitude, and
    /// PORTE's window below the interim still caps the climb.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_InterimOnRestrictedSid_ClimbsViaExceptMaintain()
    {
        AircraftState ac = CndelRejoinShadow(6_000, 1_500, s => s with { InterimAltitudeFt = 12_000 });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("direct PORTE", result.Message, StringComparison.Ordinal);
        Assert.Equal(12_000, ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(12_000, ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.Equal(10_000, ac.Targets.TargetAltitude);
        Assert.Equal(FlightPhysics.ClimbViaAltitude(ac), ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// An assumed CNDEL5 departure observed level at 6,000 with no feed altitude gets no climb via: it keeps the level
    /// altitude as its assigned altitude.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_ObservedLevel_NoClimbVia_KeepsLevel()
    {
        AircraftState ac = CndelRejoinShadow(6_000, 0, s => s);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("direct PORTE", result.Message, StringComparison.Ordinal);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(6_000, ac.Targets.AssignedAltitude);
        Assert.Equal(6_000, ac.Targets.TargetAltitude);
        Assert.False(AltitudeRestrictionInForce.ProcedureAltitudesInForce(ac));
    }

    /// <summary>
    /// An assumed NIMI5 departure (a radar-vectors SID) in its initial climb with no feed altitude gets no climb via: it
    /// keeps the old vertical seed, climbing to the filed cruise as its assigned altitude.
    /// </summary>
    [Fact]
    public void AssumedRvSidDeparture_NoClimbVia()
    {
        AircraftState ac = Koak30InitialClimbShadow("NIMI5 OAK V6 SAC", "KSAC", s => s);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("restrictions active", result.Message, StringComparison.Ordinal);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(35_000, ac.Targets.AssignedAltitude);
        Assert.Equal(35_000, ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// An assumed KATFH4 departure (filed "ROWLY VYYDA KATFH4 SUSEY") climbing near KOAK with no feed altitude rejoins its
    /// route at VYYDA and gets the climb via: the top is FL190, since KATFH's at or above 6,000 floor sits above the SID's
    /// highest ceiling, and VYYDA's at or below 4,000, on the rejoined route and in force, caps the via target below it.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_RejoinNearDeparture_ViaTargetCappedBySidFix()
    {
        AircraftState ac = RejoinShadow("ROWLY VYYDA KATFH4 SUSEY", "ROWLY", "VYYDA", 2_500, 1_500, s => s);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("direct VYYDA", result.Message, StringComparison.Ordinal);
        Assert.Equal(19_000, ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Targets.AssignedAltitude);
        NavigationTarget vyyda = ac.Targets.NavigationRoute.First(f => f.Name == "VYYDA");
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 4_000), vyyda.AltitudeRestriction);
        Assert.Equal(4_000, ac.Targets.TargetAltitude);
        Assert.Equal(FlightPhysics.ClimbViaAltitude(ac), ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// An assumed CNDEL5 departure descending near KOAK with no feed altitude is not climbing, so it gets no climb via.
    /// </summary>
    [Fact]
    public void AssumedSidDeparture_DescendingNearDeparture_NoClimbVia()
    {
        AircraftState ac = CndelRejoinShadow(6_000, -1_000, s => s);

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("direct PORTE", result.Message, StringComparison.Ordinal);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.False(AltitudeRestrictionInForce.ProcedureAltitudesInForce(ac));
    }

    /// <summary>
    /// The filed-cruise exclusion is for the SID-departure via choice only: an arrival descending onto a feed assigned
    /// altitude equal to its filed cruise still takes it, so it levels there.
    /// </summary>
    [Fact]
    public void AssumedArrival_AssignedEqualsCruise_StillLevels()
    {
        AircraftState ac = EnRouteShadow(36_000, -1_500, s => s with { AssignedAltitudeFt = 35_000 });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(35_000, ac.Targets.TargetAltitude);
        Assert.Equal(35_000, ac.Targets.AssignedAltitude);
    }

    /// <summary>An assumed aircraft observed level with a feed controller altitude takes that altitude as its assigned altitude.</summary>
    [Fact]
    public void AssumedAircraft_LevelWithFeedAltitude_TakesFeedAltitude()
    {
        AircraftState ac = EnRouteShadow(12_000, 0, s => s with { AssignedAltitudeFt = 14_000 });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(14_000, ac.Targets.TargetAltitude);
        Assert.Equal(14_000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// An assumed aircraft climbing above its feed controller altitude takes that altitude as its assigned altitude; present
    /// altitude does not replace it.
    /// </summary>
    [Fact]
    public void AssumedAircraft_ClimbingAboveFeedAltitude_TakesFeedAltitude()
    {
        AircraftState ac = EnRouteShadow(8_000, 1_500, s => s with { AssignedAltitudeFt = 7_000 });

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(7_000, ac.Targets.TargetAltitude);
        Assert.Equal(7_000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A departure out of KOAK runway 30 on <paramref name="route"/> to <paramref name="destination"/>, filed at 35,000,
    /// 1.5 nm past the DER and 900 ft up climbing 2,500 fpm, its feed sample shaped by <paramref name="feed"/>.
    /// </summary>
    private static AircraftState Koak30InitialClimbShadow(string route, string destination, Func<LiveTrafficSample, LiveTrafficSample> feed)
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        var der = new LatLon(runway.EndLatitude, runway.EndLongitude);
        LatLon pos = GeoMath.ProjectPoint(der, runway.TrueHeading, 1.5);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = destination,
            Route = route,
            Altitude = PlannedAltitude.Ifr(35_000),
        };
        return Shadow(feed(Sample(0, pos, runway.ElevationFt + 900, 160, runway.TrueHeading.Degrees, 2_500)), plan);
    }

    /// <summary>
    /// A KOAK departure to KLAS filed <see cref="CndelRejoinRoute"/> at 35,000, on the leg LEJAY-PORTE (PORTE at or below
    /// 10,000, CNDEL5's highest published altitude), as <see cref="RejoinShadow"/> places it.
    /// </summary>
    private static AircraftState CndelRejoinShadow(double altitudeFt, double verticalSpeedFpm, Func<LiveTrafficSample, LiveTrafficSample> feed) =>
        RejoinShadow(CndelRejoinRoute, "LEJAY", "PORTE", altitudeFt, verticalSpeedFpm, feed);

    /// <summary>
    /// CNDEL5 filed after LEJAY, the SID's first fix: the expanded SID alone (PORTE SUSEY) never rejoins at a SID fix, as
    /// the rejoin never picks a route's first fix.
    /// </summary>
    private const string CndelRejoinRoute = "LEJAY CNDEL5 SUSEY";

    /// <summary>
    /// A KOAK departure to KLAS filed <paramref name="route"/> at 35,000, 2.5 nm before <paramref name="toFix"/> on the leg
    /// from <paramref name="fromFix"/> and tracking to it at <paramref name="altitudeFt"/> and
    /// <paramref name="verticalSpeedFpm"/>, away from the field, so the assumer rejoins the route at
    /// <paramref name="toFix"/> rather than seeding an initial climb; its feed sample shaped by <paramref name="feed"/>.
    /// </summary>
    private static AircraftState RejoinShadow(
        string route,
        string fromFix,
        string toFix,
        double altitudeFt,
        double verticalSpeedFpm,
        Func<LiveTrafficSample, LiveTrafficSample> feed
    )
    {
        List<string> fixes = RouteExpander.Expand(route, NavigationDatabase.Instance, includeAllTransitionsOnMismatch: false);
        int to = fixes.IndexOf(toFix);
        Assert.True((to >= 1) && (fixes[to - 1] == fromFix), $"setup: the expanded route must fly {fromFix} then {toFix}: {string.Join(" ", fixes)}");
        LatLon from = FixPosition(fromFix);
        LatLon toPos = FixPosition(toFix);
        double legNm = GeoMath.DistanceNm(from, toPos);
        Assert.True(legNm > 2.5, $"setup: the leg {fromFix}-{toFix} is {legNm:F1} nm");
        LatLon pos = GeoMath.ProjectPoint(from, new TrueHeading(GeoMath.BearingTo(from, toPos)), legNm - 2.5);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KLAS",
            Route = route,
            Altitude = PlannedAltitude.Ifr(35_000),
        };
        return Shadow(feed(Sample(0, pos, altitudeFt, 220, GeoMath.BearingTo(pos, toPos), verticalSpeedFpm)), plan);
    }

    /// <summary>An en-route IFR aircraft near Stockton filed at 35,000, its feed sample shaped by <paramref name="feed"/>.</summary>
    private static AircraftState EnRouteShadow(double altitudeFt, double verticalSpeedFpm, Func<LiveTrafficSample, LiveTrafficSample> feed)
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KSFO",
            Destination = "KSMF",
            Altitude = PlannedAltitude.Ifr(35_000),
        };
        return Shadow(feed(Sample(0, EnRoute, altitudeFt, 280, 90, verticalSpeedFpm)), plan);
    }

    private static LatLon FixPosition(string name)
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.ResolveFixOrFrd(name);
        Assert.True(pos.HasValue, $"setup: {name} must resolve");
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    [Fact]
    public void EmergencySquawk_IsPreservedAndNoted()
    {
        AircraftState ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0) with { BeaconCode = 7600 }, new AircraftFlightPlan { HasFlightPlan = true });

        Assume(ac);

        Assert.Equal(7600u, ac.Transponder.Code);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("7600", StringComparison.Ordinal));
    }

    [Fact]
    public void Coasting_AssumesFromTheDeadReckonedPose_WithANote()
    {
        // The feed went quiet 50 s before "now" (elapsed 0, where the dispatch context clock sits).
        AircraftState ac = Shadow(Sample(-50, EnRoute, 11_000, 300, 90, 0), new AircraftFlightPlan { HasFlightPlan = true });
        LiveTrafficKinematics.Advance(ac, 50, null, 0, MagneticDeclination.EvaluationDateUtc);
        Assert.True(ac.LiveTraffic!.IsCoasting);
        LatLon pose = ac.Position;

        CommandResult result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(pose, ac.Position);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("coasting", StringComparison.Ordinal));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An approach follower that keeps its approach spaces on its lead by speed before final approach speed: on a course
/// intercept, on an approach's fixes and on final before the FAS bleed. Spacing works about the lead's speed, only slows the
/// follower (never above the phase's ceiling, never below its approach speed), stands down for an explicit speed and a
/// lateral-only join and inside the stabilization window, restores the ceiling when the lead lands, and an approach follower
/// unable to hold spacing at its floor drops only the follow. Real KOAK 28R navdata and the real KOAK ILS 28R clearance.
/// </summary>
public class FollowPreFinalSpacingTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";
    private const string RadarSeparationRequired = $"{Follower} visual separation terminated — radar separation required";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("AirborneFollowHelper", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");

    /// <summary>A point <paramref name="alongNm"/> out the final and <paramref name="rightNm"/> right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double rightNm) =>
        GeoMath.ProjectPoint(
            GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), alongNm),
            rwy.TrueHeading + 90.0,
            rightNm
        );

    private static AircraftState MakeAircraft(string callsign, string type, LatLon position, TrueHeading heading, double altitude, double ias) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
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

    private static void FileIfr(AircraftState ac)
    {
        ac.FlightPlan.HasFlightPlan = true;
        ac.FlightPlan.FlightRules = "IFR";
    }

    /// <summary>A lead with no phases, holding its heading and speed: spacing works on it the same whatever it flies.</summary>
    private static AircraftState AddLead(SimulationEngine engine, string type, LatLon position, TrueHeading heading, double ias)
    {
        AircraftState lead = MakeAircraft(Leader, type, position, heading, 2000, ias);
        engine.World.AddAircraft(lead);
        return lead;
    }

    /// <summary>A lead <paramref name="nm"/> straight ahead of <paramref name="follower"/> on its heading.</summary>
    private static AircraftState AddLeadAhead(SimulationEngine engine, AircraftState follower, double nm, string type, double ias) =>
        AddLead(engine, type, GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, nm), follower.TrueHeading, ias);

    /// <summary>A lead on the 28R final <paramref name="alongNm"/> out and <paramref name="rightNm"/> right, flying the runway heading.</summary>
    private static AircraftState AddLeadOffFinal(SimulationEngine engine, string type, double alongNm, double rightNm, double ias) =>
        AddLead(engine, type, OffFinal(Oak28R(), alongNm, rightNm), Oak28R().TrueHeading, ias);

    /// <summary>
    /// <paramref name="follower"/> cleared to land on 28R flying <paramref name="approachPhase"/>, then final and landing, started,
    /// following <see cref="Leader"/> with the traffic in sight.
    /// </summary>
    private static AircraftState AddFollower(SimulationEngine engine, AircraftState follower, Phase approachPhase, ApproachClearance? clearance)
    {
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList
        {
            AssignedRunway = Oak28R(),
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
            ActiveApproach = clearance,
        };
        follower.Phases.Add(approachPhase);
        if (approachPhase is not FinalApproachPhase)
        {
            follower.Phases.Add(new FinalApproachPhase());
        }

        follower.Phases.Add(new LandingPhase());
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = Leader;
        return follower;
    }

    /// <summary>A <paramref name="type"/> on the 28R intercept <paramref name="alongNm"/> out, <paramref name="rightNm"/> right, parallel.</summary>
    private static AircraftState AddInterceptFollower(SimulationEngine engine, string type, double alongNm, double rightNm, double ias)
    {
        RunwayInfo rwy = Oak28R();
        AircraftState follower = MakeAircraft(Follower, type, OffFinal(rwy, alongNm, rightNm), rwy.TrueHeading, 900, ias);
        var intercept = new InterceptCoursePhase
        {
            FinalApproachCourse = rwy.TrueHeading,
            ThresholdLat = rwy.ThresholdLatitude,
            ThresholdLon = rwy.ThresholdLongitude,
            AssignedInterceptHeading = null,
        };
        AddFollower(engine, follower, intercept, null);
        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    /// <summary>A <paramref name="type"/> on the 28R final <paramref name="alongNm"/> out on <see cref="FinalApproachPhase"/>.</summary>
    private static AircraftState AddFinalFollower(SimulationEngine engine, FinalPosition at, string type, ApproachClearance? clearance)
    {
        RunwayInfo rwy = Oak28R();
        AircraftState follower = MakeAircraft(Follower, type, OffFinal(rwy, at.AlongNm, 0.0), rwy.TrueHeading, at.Altitude, at.Ias);
        AddFollower(engine, follower, new FinalApproachPhase { SkipInterceptCheck = true }, clearance);
        Assert.IsType<FinalApproachPhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    /// <summary>A B738 16 nm out on final at 200 kt (no faster beneath the SFO Class B shelf, 14 CFR 91.117(c)), 2.75 nm behind a B738.</summary>
    private static AircraftState AddB738FinalFollowerInTrail(SimulationEngine engine, ApproachClearance? clearance)
    {
        AircraftState follower = AddFinalFollower(engine, new FinalPosition(16.0, 4000, 200), "B738", clearance);
        // 2.75 nm in trail of the 3 nm a B738 lead wants: inside the spacing, outside the S-turn deadband.
        AddLeadOffFinal(engine, "B738", 13.25, 0.0, 180);
        return follower;
    }

    /// <summary>Where on the 28R final an aircraft starts: <paramref name="AlongNm"/> out on the centerline, its altitude and speed.</summary>
    private sealed record FinalPosition(double AlongNm, double Altitude, double Ias);

    /// <summary>
    /// <paramref name="type"/> 4 nm short of VPCOL on the 28R final course, heading for it, cleared for the KOAK ILS 28R direct
    /// VPCOL by the real CAPP chain and advanced to its <see cref="ApproachNavigationPhase"/>.
    /// </summary>
    private static AircraftState ClearedI28R(string type, double ias)
    {
        (double Lat, double Lon) fix = NavigationDatabase.Instance.GetFixPosition("VPCOL") ?? throw new InvalidOperationException("VPCOL missing");
        var vpcol = new LatLon(fix.Lat, fix.Lon);
        LatLon position = GeoMath.ProjectPoint(vpcol, Oak28R().TrueHeading.ToReciprocal(), 4.0);
        AircraftState ac = MakeAircraft(Follower, type, position, new TrueHeading(GeoMath.BearingTo(position, vpcol)), 3000, ias);
        ac.Declination = 13.0;
        ac.Procedure = new AircraftProcedure { DestinationRunway = null };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "VPCOL", Position = vpcol });
        var cmd = new ClearedApproachCommand(
            "I28R",
            "KOAK",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: "VPCOL",
            DctFixLat: fix.Lat,
            DctFixLon: fix.Lon,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        CommandResult capp = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(capp.Success, capp.Message);
        return ac;
    }

    /// <summary>The real KOAK ILS 28R clearance, for a phase list built by hand.</summary>
    private static ApproachClearance RealI28RClearance() =>
        ClearedI28R("C172", 110).Phases?.ActiveApproach ?? throw new InvalidOperationException("CAPP I28R set no clearance");

    /// <summary>
    /// <see cref="ClearedI28R"/> added to <paramref name="engine"/>, flying the approach's fixes and following <see cref="Leader"/>.
    /// The first fix carries a 200 kt limit, which latches the spacing ceiling at 200 kt.
    /// </summary>
    private static AircraftState AddClearedI28RFollower(SimulationEngine engine, string type, double ias)
    {
        AircraftState ac = ClearedI28R(type, ias);
        engine.World.AddAircraft(ac);
        PhaseList phases = ac.Phases!;
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac);
        if (phases.CurrentPhase is { Status: PhaseStatus.Pending })
        {
            phases.Start(ctx);
        }

        while ((phases.CurrentPhase is not null) && (phases.CurrentPhase is not ApproachNavigationPhase))
        {
            phases.AdvanceToNext(ctx);
        }

        Assert.IsType<ApproachNavigationPhase>(phases.CurrentPhase);
        Assert.NotNull(phases.ActiveApproach);
        ac.Approach.HasReportedTrafficInSight = true;
        ac.Approach.FollowingCallsign = Leader;
        return ac;
    }

    /// <summary>
    /// A C172 on the 28R centerline 12 nm out, flying <paramref name="fixes"/> under the real KOAK ILS 28R clearance. The fix list is
    /// built by hand because the tests need fix speed limits where the real approach has none.
    /// </summary>
    private static AircraftState AddHandBuiltApproachNavFollower(SimulationEngine engine, double alongNm, double ias, List<ApproachFix> fixes)
    {
        RunwayInfo rwy = Oak28R();
        AircraftState follower = MakeAircraft(Follower, "C172", OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, 2500, ias);
        AddFollower(engine, follower, new ApproachNavigationPhase { Fixes = fixes }, RealI28RClearance());
        Assert.IsType<ApproachNavigationPhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    private static ApproachFix FixOnFinal(string name, double alongNm, int? speedKts)
    {
        LatLon fix = OffFinal(Oak28R(), alongNm, 0.0);
        return new ApproachFix(name, fix.Lat, fix.Lon, null, speedKts);
    }

    private static double ApproachSpeed(string type) => AircraftPerformance.ApproachSpeed(type, AircraftCategorization.Categorize(type));

    private static double InterceptCeiling(string type) => ApproachSpeed(type) * InterceptCoursePhase.InterceptSpeedFasMultiplier;

    private static double? SpacingCeiling(Phase? phase) =>
        phase?.ToSnapshot() switch
        {
            ApproachNavigationPhaseDto nav => nav.SpacingCeilingKts,
            FinalApproachPhaseDto final => final.SpacingCeilingKts,
            _ => throw new InvalidOperationException($"{phase?.Name} has no spacing ceiling"),
        };

    private void TickSeconds(SimulationEngine engine, AircraftState follower, double seconds)
    {
        for (int i = 0; i < (int)(seconds * 4); i++)
        {
            engine.TickPhysics(0.25);
        }

        string phase = follower.Phases?.CurrentPhase?.Name ?? "none";
        double? target = follower.Targets.TargetSpeed;
        output.WriteLine($"{phase}: IAS={follower.IndicatedAirspeed:F1} target={target:F1} following={follower.Approach.FollowingCallsign}");
    }

    // ─── Course intercept ───

    /// <summary>A B738 0.7 nm behind a C172 (1 nm wanted): the slow lead pulls it down to its approach speed and no further.</summary>
    [Fact]
    public void InterceptCourse_FollowerCloserThanSpacing_SlowsBelowCeilingNotBelowFloor()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddInterceptFollower(engine, "B738", 12.0, 1.6, 200);
        AddLeadOffFinal(engine, "C172", 11.5, 1.1, 90);
        double floor = ApproachSpeed("B738");

        TickSeconds(engine, follower, 1.0);

        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.NotNull(follower.Targets.TargetSpeed);
        Assert.Equal(floor, follower.Targets.TargetSpeed.Value, 3);
        Assert.True(floor < InterceptCeiling("B738"));
    }

    /// <summary>Two B738s in trail on the intercept, 2.5 nm apart, the lead at its approach speed: the follower holds the gap.</summary>
    [Fact]
    public void InterceptCourse_FollowerBehindLeadAtApproachSpeed_HoldsTheGap()
    {
        SimulationEngine engine = BuildEngine();
        double approachSpeed = ApproachSpeed("B738");
        AircraftState follower = AddInterceptFollower(engine, "B738", 12.0, 1.6, approachSpeed);
        AircraftState lead = AddLeadOffFinal(engine, "B738", 9.5, 1.6, approachSpeed);

        TickSeconds(engine, follower, 120.0);

        double gapNm = GeoMath.DistanceNm(follower.Position, lead.Position);
        output.WriteLine($"gap {gapNm:F3} nm");
        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.True(gapNm >= 2.5 - 1e-3, $"gap {gapNm:F3} nm");
    }

    /// <summary>A guard: an explicit ATC speed is the controller's, and spacing that was running never overwrites it.</summary>
    [Fact]
    public void InterceptCourse_NoExplicitSpeedGate_ExplicitSpeedIsNotOverwritten()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddInterceptFollower(engine, "C172", 6.0, 0.8, 90);
        AddLeadOffFinal(engine, "C172", 5.6, 0.4, 90);
        TickSeconds(engine, follower, 0.25);
        Assert.True(follower.Targets.TargetSpeed < InterceptCeiling("C172") - 5.0, "spacing has not run");

        follower.Targets.HasExplicitSpeedCommand = true;
        follower.Targets.TargetSpeed = 100;
        TickSeconds(engine, follower, 0.25);

        Assert.Equal(100, follower.Targets.TargetSpeed);
    }

    /// <summary>A guard: 2 nm or more off the course the follower is not yet joining it, so it does not space.</summary>
    [Fact]
    public void InterceptCourse_CrossTrackTwoNmOrMore_DoesNotSpace()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddInterceptFollower(engine, "C172", 6.0, 1.5, 90);
        AircraftState lead = AddLeadOffFinal(engine, "C172", 5.6, 1.1, 90);
        double ceiling = InterceptCeiling("C172");
        TickSeconds(engine, follower, 0.25);

        follower.Position = OffFinal(rwy, 6.0, 2.5);
        lead.Position = OffFinal(rwy, 5.6, 2.1);
        follower.Targets.TargetSpeed = ceiling;
        TickSeconds(engine, follower, 0.25);

        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(ceiling, follower.Targets.TargetSpeed);
    }

    /// <summary>A guard: 51 s from the threshold the follower is inside the stabilization window and its speed is left alone.</summary>
    [Fact]
    public void InterceptCourse_InsideStabilizationWindow_DoesNotSpace()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddInterceptFollower(engine, "C172", 1.0, 0.8, 90);
        AddLeadOffFinal(engine, "C172", 0.6, 0.4, 90);

        TickSeconds(engine, follower, 0.25);

        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(InterceptCeiling("C172"), follower.Targets.TargetSpeed);
    }

    // ─── Approach fixes ───

    [Fact]
    public void ApproachNavigation_FollowerCloserThanSpacing_SlowsBelowLatchedCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedI28RFollower(engine, "C172", 110);
        AddLeadAhead(engine, follower, 0.6, "C172", 90);

        TickSeconds(engine, follower, 1.0);

        Assert.IsType<ApproachNavigationPhase>(follower.Phases!.CurrentPhase);
        Assert.NotNull(follower.Targets.TargetSpeed);
        Assert.InRange(follower.Targets.TargetSpeed.Value, ApproachSpeed("C172"), 110 - 5.0);
    }

    [Fact]
    public void ApproachNavigation_FixSpeedReplacesTheCeiling()
    {
        SimulationEngine engine = BuildEngine();
        List<ApproachFix> fixes = [FixOnFinal("NEAR", 8.4, null), FixOnFinal("SLOW", 5.0, 95)];
        AircraftState follower = AddHandBuiltApproachNavFollower(engine, 9.0, 110, fixes);
        // At 105 kt the lead's speed sits between the 95 kt fix limit and the 110 kt first latch.
        AddLeadOffFinal(engine, "C172", 8.4, 0.0, 105);
        var nav = (ApproachNavigationPhase)follower.Phases!.CurrentPhase!;
        TickSeconds(engine, follower, 0.25);

        for (int i = 0; (i < 40) && (nav.CurrentFixIndex == 0); i++)
        {
            TickSeconds(engine, follower, 0.25);
        }

        Assert.Equal(1, nav.CurrentFixIndex);
        Assert.Same(nav, follower.Phases.CurrentPhase);
        Assert.Equal(95, SpacingCeiling(nav));
        Assert.NotNull(follower.Targets.TargetSpeed);
        Assert.InRange(follower.Targets.TargetSpeed.Value, ApproachSpeed("C172"), 95 - 1.0);
    }

    /// <summary>An at-or-below 120 kt limit at a fix ahead is a cap, never a cue for a following C172 at 110 kt to speed up.</summary>
    [Fact]
    public void ApproachNavigation_AtOrBelowFixLimitAhead_DoesNotSpeedUpTheFollower()
    {
        SimulationEngine engine = BuildEngine();
        List<ApproachFix> fixes = [FixOnFinal("A", 9.0, null), FixOnFinal("B", 6.0, 120)];
        AircraftState follower = AddHandBuiltApproachNavFollower(engine, 12.0, 110, fixes);
        AddLeadOffFinal(engine, "C172", 11.4, 0.0, 90);

        for (int i = 0; i < 4; i++)
        {
            engine.TickPhysics(0.25);
            double? target = follower.Targets.TargetSpeed;
            Assert.True(target is <= 110.0, $"tick {i}: target {target:F1} kt above the 110 kt latched ceiling");
        }

        Assert.True(follower.Targets.TargetSpeed < 105.0, $"target {follower.Targets.TargetSpeed:F1} kt");
    }

    [Fact]
    public void ApproachNavigation_SpacingCeilingRoundTripsThroughSnapshot()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedI28RFollower(engine, "B738", 220);
        AddLeadAhead(engine, follower, 6.0, "B738", 250);
        TickSeconds(engine, follower, 0.25);
        Assert.Equal(200, SpacingCeiling(follower.Phases!.CurrentPhase));

        PhaseListDto dto = follower.Phases.ToSnapshot();
        follower.Phases = PhaseList.FromSnapshot(dto, null);
        follower.IndicatedAirspeed = 190;
        follower.Targets.TargetSpeed = null;
        TickSeconds(engine, follower, 0.25);

        Assert.IsType<ApproachNavigationPhase>(follower.Phases.CurrentPhase);
        Assert.Equal(200, follower.Targets.TargetSpeed);
    }

    /// <summary>A recording made before the spacing ceilings existed restores with no ceiling, and the first spacing tick latches one.</summary>
    [Fact]
    public void SnapshotJson_WithoutSpacingCeilings_RestoresNullAndRelatches()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedI28RFollower(engine, "B738", 220);
        AddLeadAhead(engine, follower, 6.0, "B738", 250);
        TickSeconds(engine, follower, 0.25);
        Assert.Equal(200, SpacingCeiling(follower.Phases!.CurrentPhase));
        string json = JsonSerializer.Serialize(follower.Phases.ToSnapshot(), RecordingJsonOptions.Default);

        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidOperationException("empty snapshot");
        RemoveSpacingCeilings(node);
        PhaseListDto restored = node.Deserialize<PhaseListDto>(RecordingJsonOptions.Default) ?? throw new InvalidOperationException("no dto");
        Assert.All(restored.Phases.OfType<ApproachNavigationPhaseDto>(), nav => Assert.Null(nav.SpacingCeilingKts));
        Assert.All(restored.Phases.OfType<FinalApproachPhaseDto>(), final => Assert.Null(final.SpacingCeilingKts));
        follower.Phases = PhaseList.FromSnapshot(restored, null);
        follower.IndicatedAirspeed = 190;
        follower.Targets.TargetSpeed = null;
        TickSeconds(engine, follower, 0.25);

        Assert.Equal(190, SpacingCeiling(follower.Phases.CurrentPhase));
    }

    private static void RemoveSpacingCeilings(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).Where(k => k.Equals("SpacingCeilingKts", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    obj.Remove(key);
                }

                foreach (JsonNode? child in obj.Select(p => p.Value).ToList())
                {
                    if (child is not null)
                    {
                        RemoveSpacingCeilings(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (JsonNode? child in array)
                {
                    if (child is not null)
                    {
                        RemoveSpacingCeilings(child);
                    }
                }

                break;
        }
    }

    [Fact]
    public void ApproachNavigation_ExplicitSpeed_DropsTheCeilingAndRelatchesAfter()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedI28RFollower(engine, "C172", 110);
        AddLeadAhead(engine, follower, 0.6, "C172", 90);
        TickSeconds(engine, follower, 0.25);
        Phase? nav = follower.Phases!.CurrentPhase;
        Assert.Equal(200, SpacingCeiling(nav));

        follower.Targets.HasExplicitSpeedCommand = true;
        follower.Targets.TargetSpeed = 100;
        TickSeconds(engine, follower, 0.25);
        Assert.Null(SpacingCeiling(nav));

        follower.Targets.HasExplicitSpeedCommand = false;
        double relatchKts = follower.Targets.TargetSpeed ?? follower.IndicatedAirspeed;
        TickSeconds(engine, follower, 0.25);
        Assert.Equal(relatchKts, SpacingCeiling(nav));
        Assert.True(relatchKts < 200.0);
    }

    // ─── Final before the FAS bleed ───

    [Fact]
    public void FinalApproach_BeforeFas_FollowerCloserThanSpacing_SlowsWithoutSettingConfigOrFlap()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddB738FinalFollowerInTrail(engine, null);
        var final = (FinalApproachPhase)follower.Phases!.CurrentPhase!;

        TickSeconds(engine, follower, 1.0);

        Assert.Same(final, follower.Phases.CurrentPhase);
        Assert.NotNull(follower.Targets.TargetSpeed);
        Assert.InRange(follower.Targets.TargetSpeed.Value, ApproachSpeed("B738"), 200 - 3.0);
        var dto = (FinalApproachPhaseDto)final.ToSnapshot();
        Assert.False(dto.FasSet);
        Assert.False(dto.ConfigSet);
        Assert.False(dto.FlapSet);
    }

    /// <summary>A guard: a VFR pattern follower on final before FAS spaces by the same decrease-only rule.</summary>
    [Fact]
    public void FinalApproach_BeforeFas_VfrPatternFollower_Spaces()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = MakeAircraft(Follower, "C172", OffFinal(rwy, 7.0, 0.0), rwy.TrueHeading, 2000, 100);
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase());
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = Leader;
        // 0.75 nm of the 1 nm a C172 lead wants: outside the S-turn deadband.
        AddLeadOffFinal(engine, "C172", 6.25, 0.0, 90);

        TickSeconds(engine, follower, 1.0);

        Assert.IsType<FinalApproachPhase>(follower.Phases.CurrentPhase);
        Assert.True(follower.Targets.TargetSpeed < 95.0, $"target {follower.Targets.TargetSpeed:F1} kt");
    }

    [Fact]
    public void FinalApproach_ResumedAfterSTurn_KeepsTheSpacingCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddB738FinalFollowerInTrail(engine, null);
        var final = (FinalApproachPhase)follower.Phases!.CurrentPhase!;
        TickSeconds(engine, follower, 0.25);
        Assert.Equal(200, SpacingCeiling(final));

        follower.Targets.TargetSpeed = 150;
        FinalApproachPhase resume = final.CloneForResume();
        resume.OnStart(CommandDispatcher.BuildMinimalContext(follower));

        Assert.Equal(200, SpacingCeiling(resume));
    }

    [Fact]
    public void FinalApproach_ExplicitSpeed_DropsTheCeilingAndRelatchesAfter()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddB738FinalFollowerInTrail(engine, null);
        Phase? final = follower.Phases!.CurrentPhase;
        TickSeconds(engine, follower, 0.25);
        Assert.Equal(200, SpacingCeiling(final));

        follower.Targets.HasExplicitSpeedCommand = true;
        follower.Targets.TargetSpeed = 180;
        TickSeconds(engine, follower, 0.25);
        Assert.Null(SpacingCeiling(final));

        follower.Targets.HasExplicitSpeedCommand = false;
        TickSeconds(engine, follower, 0.25);
        Assert.Equal(180, SpacingCeiling(final));
    }

    [Fact]
    public void FinalApproach_SpacingCeilingRoundTripsThroughSnapshot()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddB738FinalFollowerInTrail(engine, null);
        TickSeconds(engine, follower, 0.25);
        var dto = (FinalApproachPhaseDto)follower.Phases!.CurrentPhase!.ToSnapshot();
        Assert.Equal(200, dto.SpacingCeilingKts);

        var restored = FinalApproachPhase.FromSnapshot(dto);

        Assert.Equal(200, SpacingCeiling(restored));
    }

    /// <summary>A guard: a JFAC/JLOC join authorizes the course and nothing else, so the follower keeps its speed.</summary>
    [Fact]
    public void FinalApproach_LateralInterceptOnly_DoesNotSpace()
    {
        SimulationEngine engine = BuildEngine();
        ApproachClearance clearance = RealI28RClearance();
        clearance.LateralInterceptOnly = true;
        AircraftState follower = AddB738FinalFollowerInTrail(engine, clearance);

        TickSeconds(engine, follower, 1.0);

        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Null(follower.Targets.TargetSpeed);
    }

    // ─── Every site ───

    /// <summary>
    /// A lead far ahead and faster asks for more speed than the phase allows: the follower, slowing to the 200 kt ceiling on the
    /// approach's fixes, is held there.
    /// </summary>
    [Fact]
    public void Spacing_NeverWritesAboveTheCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedI28RFollower(engine, "B738", 250);
        follower.Targets.TargetSpeed = 200;
        AddLeadAhead(engine, follower, 6.0, "B738", 250);

        for (int i = 0; i < 24; i++)
        {
            engine.TickPhysics(0.25);
            double? target = follower.Targets.TargetSpeed;
            Assert.True(target is <= 200.0, $"tick {i}: target {target:F1} kt, ceiling 200 kt");
        }

        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(200.0, follower.Targets.TargetSpeed);
    }

    [Fact]
    public void Spacing_LeadOnGround_RestoresTheCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddInterceptFollower(engine, "C172", 6.0, 0.8, 90);
        AircraftState lead = AddLeadOffFinal(engine, "C172", 5.6, 0.4, 90);
        double ceiling = InterceptCeiling("C172");
        TickSeconds(engine, follower, 1.0);
        Assert.True(follower.Targets.TargetSpeed < ceiling - 5.0, $"spacing target {follower.Targets.TargetSpeed:F1} kt, ceiling {ceiling:F1} kt");

        lead.IsOnGround = true;
        TickSeconds(engine, follower, 0.25);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        Assert.NotNull(follower.Targets.TargetSpeed);
        Assert.Equal(ceiling, follower.Targets.TargetSpeed.Value, 3);
    }

    [Fact]
    public void ApproachNavigation_LeadOnGround_RestoresTheCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedI28RFollower(engine, "B738", 220);
        AircraftState lead = AddLeadAhead(engine, follower, 2.5, "B738", 180);
        TickSeconds(engine, follower, 1.0);
        Assert.True(follower.Targets.TargetSpeed < 195.0, $"spacing target {follower.Targets.TargetSpeed:F1} kt");

        lead.IsOnGround = true;
        TickSeconds(engine, follower, 0.25);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<ApproachNavigationPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(200, follower.Targets.TargetSpeed);
    }

    [Fact]
    public void FinalApproach_BeforeFas_LeadOnGround_RestoresTheCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFinalFollower(engine, new FinalPosition(16.0, 4000, 200), "B738", null);
        AircraftState lead = AddLeadOffFinal(engine, "B738", 13.25, 0.0, 180);
        TickSeconds(engine, follower, 1.0);
        Assert.True(follower.Targets.TargetSpeed < 197.0, $"spacing target {follower.Targets.TargetSpeed:F1} kt");

        lead.IsOnGround = true;
        TickSeconds(engine, follower, 0.25);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<FinalApproachPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(200, follower.Targets.TargetSpeed);
    }

    // ─── Unable to maintain separation ───

    /// <summary>
    /// An IFR follower on an approach's fixes 0.4 nm behind a C172 at 70 kt (1 nm wanted) cannot open the gap above its approach speed:
    /// the follow ends, the approach, landing clearance and traffic-in-sight report stay, and the instructor is told radar
    /// separation applies.
    /// </summary>
    [Fact]
    public void UnableToMaintain_OnKeptApproach_ClearsOnlyTheFollow()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedI28RFollower(engine, "C172", ApproachSpeed("C172") + 10.0);
        FileIfr(follower);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        AddLeadAhead(engine, follower, 0.4, "C172", 70);
        Phase? approach = follower.Phases.CurrentPhase;
        int phaseCount = follower.Phases.Phases.Count;

        TickSeconds(engine, follower, 0.25);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(approach, follower.Phases.CurrentPhase);
        Assert.Equal(phaseCount, follower.Phases.Phases.Count);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.True(follower.Approach.HasReportedTrafficInSight);
        Assert.Contains(RadarSeparationRequired, follower.PendingWarnings);
    }

    /// <summary>A C172 1.8 nm out on final at its approach speed (past the FAS trigger), 0.5 nm behind a B738, ticked once.</summary>
    private AircraftState UnableToMaintainOnFinal(bool ifr, ApproachClearance? clearance)
    {
        SimulationEngine engine = BuildEngine();
        double approachSpeed = ApproachSpeed("C172");
        AircraftState follower = AddFinalFollower(engine, new FinalPosition(1.8, 580, approachSpeed), "C172", clearance);
        if (ifr)
        {
            FileIfr(follower);
        }

        AddLeadOffFinal(engine, "B738", 1.3, 0.0, 150);
        TickSeconds(engine, follower, 0.25);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<FinalApproachPhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    [Fact]
    public void UnableToMaintain_OnFinalAfterFas_IfrWithApproachClearance_WarnsRadarSeparation()
    {
        AircraftState follower = UnableToMaintainOnFinal(true, RealI28RClearance());

        Assert.Contains(RadarSeparationRequired, follower.PendingWarnings);
    }

    [Fact]
    public void UnableToMaintain_OnFinalAfterFas_IfrWithoutApproachClearance_NoRadarLine()
    {
        AircraftState follower = UnableToMaintainOnFinal(true, null);

        Assert.DoesNotContain(RadarSeparationRequired, follower.PendingWarnings);
    }

    [Fact]
    public void UnableToMaintain_OnFinalAfterFas_VfrFollower_NoRadarLine()
    {
        AircraftState follower = UnableToMaintainOnFinal(false, RealI28RClearance());

        Assert.DoesNotContain(RadarSeparationRequired, follower.PendingWarnings);
    }
}

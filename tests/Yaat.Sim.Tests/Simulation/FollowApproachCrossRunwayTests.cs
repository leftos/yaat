using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// FOLLOW issued to a follower on an instrument approach when the lead is landing a different runway at the
/// same airport (OAK's 28L/28R pair): FOLLOW sequences arrivals onto one runway, and a re-sequence onto the
/// parallel from mid-approach is a maneuver the pilot does not expect (7110.65 §7-4-3.c.2; AIM §4-3-5).
///
/// A filed IFR follower is refused inside and outside the final approach fix — its approach is not a practice
/// one, so the controller vectors a re-sequence. A VFR follower, or one with no flight plan at all, is refused
/// inside the FAF only: established on the final, within <see cref="AirborneFollowHelper.OnFinalMaxCrossTrackNm"/>
/// of the extended centreline, at or inside the smaller of the published FAF distance and 5 NM (7110.65
/// §5-7-1.b.4's "inside the final approach fix on final"). Outside it, or off the final (abeam, on a feeder),
/// the follow falls through to the existing re-sequence onto the lead's runway. A lead on the follower's own
/// runway is not this refusal, and a lead on the ground takes the existing ground refusal first. A refusal
/// leaves the approach, its landing clearance and the follow untouched.
/// </summary>
public class FollowApproachCrossRunwayTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";

    /// <summary>How far inside and outside the FAF boundary the positioned followers sit.</summary>
    private const double LimitOffsetNm = 1.0;

    private const string CrossRunwayText = $"Unable, on approach for runway 28R, {Leader} is landing runway 28L, request vectors";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    /// <summary>The OAK ground layout the engine hands the dispatcher, for the runway end's threshold displacement.</summary>
    private static AirportGroundLayout? OakLayout => new TestAirportGroundData().GetLayout("KOAK");

    /// <summary><paramref name="rwy"/>'s published threshold displacement, in nm (0 with no layout).</summary>
    private static double DisplacementNm(RunwayInfo rwy) => LandingThreshold.DisplacementFt(rwy, OakLayout) / GeoMath.FeetPerNm;

    /// <summary>
    /// The inside-the-FAF limit the dispatcher computes for <paramref name="rwy"/>: the smaller of its published
    /// FAF distance and 5 NM. The test process loads no real CIFP into the approach gate table, so this is the
    /// 5 NM ceiling.
    /// </summary>
    private static double FafLimit(RunwayInfo rwy) =>
        ApproachGateDatabase.InsideFafLimitNm(ApproachGateDatabase.GetFafDistanceNm(rwy.AirportId, rwy.Designator, DisplacementNm(rwy)));

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading.ToReciprocal(),
            alongNm
        );
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    /// <summary>A point whose along-final distance to the <em>landing</em> threshold is <paramref name="alongNm"/> nm.</summary>
    private static LatLon OnFinalAt(RunwayInfo rwy, double alongNm) => OffFinal(rwy, alongNm - DisplacementNm(rwy), 0);

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = 90,
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

    /// <summary>A lead in <paramref name="runway"/>'s downwind circuit, positioned by <paramref name="position"/>.</summary>
    private static AircraftState AddCircuitLead(SimulationEngine engine, RunwayInfo runway, LatLon position)
    {
        AircraftState lead = MakeVfr(Leader, position, runway.TrueHeading.ToReciprocal(), 1000);
        engine.World.AddAircraft(lead);
        lead.Phases = new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Right };
        lead.Phases.Add(new DownwindPhase());
        lead.Phases.Add(new BasePhase());
        lead.Phases.Add(new FinalApproachPhase());
        lead.Phases.Add(new LandingPhase());
        return lead;
    }

    /// <summary>A lead on 28L's final, <paramref name="alongNm"/> out: the parallel to the follower's 28R.</summary>
    private static AircraftState AddParallelFinalLead(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28L");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, alongNm, 0), rwy.TrueHeading, 900);
        engine.World.AddAircraft(lead);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new FinalApproachPhase());
        lead.Phases.Add(new LandingPhase());
        return lead;
    }

    /// <summary>A lead on 28R's final, <paramref name="alongNm"/> out: the follower's own runway.</summary>
    private static AircraftState AddSameRunwayFinalLead(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, alongNm, 0), rwy.TrueHeading, 900);
        engine.World.AddAircraft(lead);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new FinalApproachPhase());
        lead.Phases.Add(new LandingPhase());
        return lead;
    }

    /// <summary>A lead on the ground at 28L's threshold, assigned to 28L.</summary>
    private static AircraftState AddGroundParallelLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28L");
        AircraftState lead = MakeVfr(Leader, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading, rwy.ElevationFt);
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
        engine.World.AddAircraft(lead);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        return lead;
    }

    /// <summary>
    /// A follower intercepting the 28R final, its along-final distance to the landing threshold
    /// <paramref name="alongNm"/> nm and <paramref name="crossNm"/> nm right of the extended centreline (0 puts it on
    /// the final, inside the cross-track limit), cleared to land.
    /// </summary>
    private static AircraftState AddApproachFollowerAt(SimulationEngine engine, RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon position = (Math.Abs(crossNm) < 1e-9) ? OnFinalAt(rwy, alongNm) : OffFinal(rwy, alongNm - DisplacementNm(rwy), crossNm);
        AircraftState follower = MakeVfr(Follower, position, rwy.TrueHeading, 900);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
        follower.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = rwy.TrueHeading,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                AssignedInterceptHeading = null,
            }
        );
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase());
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));
        Assert.IsType<InterceptCoursePhase>(follower.Phases.CurrentPhase);
        return follower;
    }

    /// <summary>
    /// A VFR follower on an approach's fix sequence, established on the 28R final <paramref name="alongNm"/> nm from the
    /// landing threshold, its one fix further in on the centreline, cleared to land. The chain is left unstarted, as
    /// <see cref="FollowSequenceRefusalTests.AddApproachNavigationFollower"/> does: the current phase is the fixes either way.
    /// </summary>
    private static AircraftState AddApproachNavFollowerOnFinal(SimulationEngine engine, RunwayInfo rwy, double alongNm)
    {
        LatLon fix = OnFinalAt(rwy, Math.Max(alongNm - 2.0, 0.5));
        AircraftState follower = MakeVfr(Follower, OnFinalAt(rwy, alongNm), rwy.TrueHeading, 900);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
        follower.Phases.Add(new ApproachNavigationPhase { Fixes = [new ApproachFix("FIX", fix.Lat, fix.Lon)] });
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase());
        return follower;
    }

    /// <summary>The follower's state before FOLLOW, to prove a refusal leaves it untouched.</summary>
    private sealed record FollowerState(PhaseList? Phases, Phase? Current, int PhaseCount, ClearanceType? Clearance, string? ClearedRunwayId);

    private static FollowerState Capture(AircraftState ac) =>
        new(ac.Phases, ac.Phases?.CurrentPhase, ac.Phases?.Phases.Count ?? 0, ac.Phases?.LandingClearance, ac.Phases?.ClearedRunwayId);

    private CommandResult Send(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand(Follower, command);
        output.WriteLine($"{command}: success={result.Success} — {result.Message}");
        return result;
    }

    /// <summary>Prints the inside-the-FAF limit and the follower's along-final distance to the landing threshold.</summary>
    private void LogLimit(AircraftState follower)
    {
        RunwayInfo rwy = follower.Phases!.AssignedRunway!;
        double alongNm = AirborneFollowHelper.AlongFinalNm(follower.Position, rwy) + DisplacementNm(rwy);
        output.WriteLine($"28R limit {FafLimit(rwy):F2} nm; follower along-final {alongNm:F2} nm, displacement {DisplacementNm(rwy):F3} nm");
    }

    private void AssertRefusedUnchanged(AircraftState follower, FollowerState before, CommandResult result)
    {
        Assert.False(result.Success, result.Message);
        Assert.Equal(CrossRunwayText, result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    /// <summary>Files <paramref name="aircraft"/> IFR: a flight plan that is not VFR, the codebase's IFR test.</summary>
    private static void AsIfr(AircraftState aircraft)
    {
        aircraft.FlightPlan.HasFlightPlan = true;
        aircraft.FlightPlan.FlightRules = "IFR";
    }

    /// <summary>An aircraft with no filed flight plan and no flight rules — neither IFR nor VFR.</summary>
    private static void AsUnfiled(AircraftState aircraft)
    {
        aircraft.FlightPlan.HasFlightPlan = false;
        aircraft.FlightPlan.FlightRules = "";
    }

    // ─── IFR: refused inside and outside the FAF ───

    [Fact]
    public void IfrFollowerOnIntercept_LeadLandingOtherRunway_OutsideFaf_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) + LimitOffsetNm, 0.8);
        AsIfr(follower);
        AddParallelFinalLead(engine, 2.0);
        FollowerState before = Capture(follower);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result);
    }

    [Fact]
    public void IfrFollowerOnIntercept_LeadLandingOtherRunway_InsideFaf_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) - LimitOffsetNm, 0.8);
        AsIfr(follower);
        AddParallelFinalLead(engine, 2.0);
        FollowerState before = Capture(follower);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result);
    }

    [Fact]
    public void IfrFollowerOnApproachNav_LeadLandingOtherRunway_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachNavigationFollower(engine);
        AsIfr(follower);
        AddParallelFinalLead(engine, 2.0);
        FollowerState before = Capture(follower);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result);
    }

    // ─── VFR: refused inside the FAF, re-sequenced outside it ───

    [Fact]
    public void VfrFollowerOnApproach_LeadLandingOtherRunway_InsideFaf_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) - LimitOffsetNm, 0);
        AddParallelFinalLead(engine, 2.0);
        FollowerState before = Capture(follower);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result);
    }

    [Fact]
    public void VfrFollowerOnApproachNav_LeadLandingOtherRunway_InsideFaf_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachNavFollowerOnFinal(engine, rwy, FafLimit(rwy) - LimitOffsetNm);
        AddParallelFinalLead(engine, 2.0);
        FollowerState before = Capture(follower);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result);
    }

    [Fact]
    public void VfrFollowerOnApproach_LeadLandingOtherRunway_OutsideFaf_IsResequenced()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) + LimitOffsetNm, 0);
        RunwayInfo parallel = Oak("28L");
        AddCircuitLead(engine, parallel, OffFinal(parallel, -1.0, 1.0));
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal("28L", follower.Phases!.AssignedRunway?.Designator);
        Assert.IsNotType<InterceptCoursePhase>(follower.Phases.CurrentPhase);
    }

    /// <summary>The common case: a practice ILS 28R outside the FAF told to follow traffic on the 28L final.</summary>
    [Fact]
    public void VfrFollowerOnApproach_LeadOnParallelFinal_OutsideFaf_IsResequenced()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) + LimitOffsetNm, 0);
        AddParallelFinalLead(engine, 2.0);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal("28L", follower.Phases!.AssignedRunway?.Designator);
        Assert.IsNotType<InterceptCoursePhase>(follower.Phases.CurrentPhase);
    }

    /// <summary>Abeam the final, inside the limit by distance but several miles off the centreline: not on final, so outside.</summary>
    [Fact]
    public void VfrFollowerOnIntercept_AbeamFinal_IsNotInsideFaf()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) - LimitOffsetNm, 3.0);
        RunwayInfo parallel = Oak("28L");
        AddCircuitLead(engine, parallel, OffFinal(parallel, -1.0, 1.0));
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.DoesNotContain("is landing runway", result.Message, StringComparison.Ordinal);
        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    // ─── A lead on the follower's own runway is not this refusal ───

    [Fact]
    public void FollowerOnApproach_SameRunwayLead_IsNotRefusedAsCrossRunway()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) - LimitOffsetNm, 0);
        AddSameRunwayFinalLead(engine, 1.5);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.DoesNotContain("is landing runway", result.Message, StringComparison.Ordinal);
        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    // ─── Chain order: a ground lead takes the ground refusal ───

    [Fact]
    public void FollowerOnApproach_GroundLeadOnOtherRunway_GetsTheGroundRefusal()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) - LimitOffsetNm, 0);
        AsIfr(follower);
        AddGroundParallelLead(engine);
        FollowerState before = Capture(follower);
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, {Leader} is on the ground", result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    // ─── No flight plan: VFR treatment ───

    [Fact]
    public void FollowerOnApproach_NoFlightPlan_IsTreatedAsVfr()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddApproachFollowerAt(engine, rwy, FafLimit(rwy) + LimitOffsetNm, 0);
        AsUnfiled(follower);
        RunwayInfo parallel = Oak("28L");
        AddCircuitLead(engine, parallel, OffFinal(parallel, -1.0, 1.0));
        LogLimit(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.DoesNotContain("is landing runway", result.Message, StringComparison.Ordinal);
        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }
}

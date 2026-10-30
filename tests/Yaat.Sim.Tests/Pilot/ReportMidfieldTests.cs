using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// YAAT-1032: <c>REPORT MIDFIELD</c> fires the pilot's midfield-downwind report from <see cref="DownwindPhase"/> at the
/// leg's midfield point, on every circuit, whatever the landing clearance. The wording follows the aircraft's position
/// when the report fires: "midfield {side} downwind" within <see cref="DownwindPhase.MidfieldReportBandNm"/> of
/// midfield, "{side} downwind, past midfield" short of abeam the threshold, and "{side} downwind, abeam the numbers"
/// from there on. Flown on the real KOAK 28R pattern; the aircraft is stepped along the downwind line tick by tick.
/// </summary>
[Collection("NavDbMutator")]
public class ReportMidfieldTests(ITestOutputHelper output)
{
    private const double StepNm = 0.02;

    private static RunwayInfo Runway(string designator)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? rwy = NavigationDatabase.Instance.GetRunway("KOAK", designator);
        Assert.NotNull(rwy);
        return rwy;
    }

    private static RunwayInfo Runway28R() => Runway("28R");

    private static PatternWaypoints Waypoints(PatternDirection dir) => Waypoints(Runway28R(), dir);

    private static PatternWaypoints Waypoints(RunwayInfo runway, PatternDirection dir) =>
        PatternGeometry.Compute(runway, AircraftCategory.Piston, "", 0, dir, null, null, null, authoredRunway: null);

    private static double AlongTrack(PatternWaypoints wp, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, new LatLon(wp.ThresholdLat, wp.ThresholdLon), wp.DownwindHeading);

    private static LatLon PointAtAlongTrack(PatternWaypoints wp, double alongTrackNm)
    {
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        double offset = alongTrackNm - AlongTrack(wp, abeam);
        return offset >= 0
            ? GeoMath.ProjectPoint(abeam, wp.DownwindHeading, offset)
            : GeoMath.ProjectPoint(abeam, wp.DownwindHeading.ToReciprocal(), -offset);
    }

    private static double DownwindStartAlongTrack(PatternWaypoints wp) => AlongTrack(wp, new LatLon(wp.DownwindStartLat, wp.DownwindStartLon));

    private static double AbeamAlongTrack(PatternWaypoints wp) => AlongTrack(wp, new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon));

    private static AircraftState MakeAircraft(PatternWaypoints wp)
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon),
            TrueHeading = wp.DownwindHeading,
            TrueTrack = wp.DownwindHeading,
            Altitude = wp.PatternAltitude,
            IndicatedAirspeed = 90,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                HasFlightPlan = true,
                Destination = "KOAK",
            },
            Phases = new PhaseList { AssignedRunway = Runway28R(), TrafficDirection = wp.Direction },
            HasMadeInitialContact = true,
        };
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, bool autoClearedToLand)
    {
        RunwayInfo runway = Runway28R();
        return new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "TWR",
            AutoClearedToLand = autoClearedToLand,
        };
    }

    /// <summary>
    /// Starts a downwind in the aircraft's phase list, after <paramref name="predecessor"/>, with the aircraft at
    /// <paramref name="startAlongTrackNm"/>. A crosswind predecessor (<see cref="AfterCrosswind"/>) makes a normal circuit;
    /// a pattern entry or midfield crossing makes a join.
    /// </summary>
    private static DownwindPhase StartDownwind(
        AircraftState ac,
        PhaseContext ctx,
        PatternWaypoints wp,
        double startAlongTrackNm,
        bool extended,
        Phase predecessor
    )
    {
        ac.Position = PointAtAlongTrack(wp, startAlongTrackNm);
        var phase = new DownwindPhase { Waypoints = wp, IsExtended = extended };
        PhaseList phases = ac.Phases!;
        phases.Add(predecessor);
        phases.Add(phase);
        while (!ReferenceEquals(phases.CurrentPhase, phase))
        {
            phases.AdvanceToNext(ctx);
        }

        return phase;
    }

    private static CrosswindPhase AfterCrosswind(PatternWaypoints wp) => new() { Waypoints = wp };

    /// <summary>A 45-degree entry to the downwind's abeam point, the join a <c>ELD</c> from outside the pattern flies.</summary>
    private static PatternEntryPhase AfterFortyFiveEntry(PatternWaypoints wp) =>
        new()
        {
            EntryLat = wp.DownwindAbeamLat,
            EntryLon = wp.DownwindAbeamLon,
            PatternAltitude = wp.PatternAltitude,
            Kind = PatternEntryKind.FortyFive,
        };

    /// <summary>
    /// Steps the aircraft along the downwind line, ticking the phase each step, up to <paramref name="untilAlongTrackNm"/>
    /// or the phase's completion. Returns the along-track at which each pilot transmission was queued.
    /// </summary>
    private static List<(double AlongTrack, PilotTransmission Tx)> Fly(
        DownwindPhase phase,
        AircraftState ac,
        PhaseContext ctx,
        PatternWaypoints wp,
        double untilAlongTrackNm
    )
    {
        var heard = new List<(double, PilotTransmission)>();
        int seen = ac.PendingPilotTransmissions.Count;
        double along = AlongTrack(wp, ac.Position);
        while (along <= untilAlongTrackNm)
        {
            bool done = phase.OnTick(ctx);
            for (; seen < ac.PendingPilotTransmissions.Count; seen++)
            {
                heard.Add((along, ac.PendingPilotTransmissions[seen]));
            }

            if (done)
            {
                break;
            }

            along += StepNm;
            ac.Position = PointAtAlongTrack(wp, along);
        }

        return heard;
    }

    /// <summary>
    /// Every downwind call the aircraft made, wherever it was routed: a transmission voiced with the solo context, or a
    /// terminal line from a command handler's minimal (RPO) context. Covers the reports in all three wordings and the
    /// unarmed midfield reminder.
    /// </summary>
    private static List<string> DownwindCalls(AircraftState ac)
    {
        List<string> all =
        [
            .. ac.PendingPilotTransmissions.Select(t => t.Text),
            .. ac.PendingWarnings,
            .. ac.PendingPilotSpeech.Select(s => s.Terminal),
        ];
        return [.. all.Where(text => text.Contains("downwind runway", StringComparison.OrdinalIgnoreCase))];
    }

    [Fact]
    public void Armed_FiresAtMidfield_WithSideAndRunway()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        List<(double AlongTrack, PilotTransmission Tx)> heard = Fly(phase, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm);

        (double firedAt, PilotTransmission tx) = Assert.Single(heard);
        output.WriteLine($"fired at along-track {firedAt:F3} (midfield {midfield:F3}): {tx.Text} / {tx.SpeechText}");
        Assert.Equal("midfield left downwind runway 28R.", tx.Text);
        Assert.Contains("midfield left downwind runway two eight right", tx.SpeechText, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(firedAt, midfield - DownwindPhase.MidfieldLeadNm, midfield - DownwindPhase.MidfieldLeadNm + StepNm);
        Assert.True(ac.Approach.ReportArmedMidfield, "the report stays armed for the next circuit");
    }

    [Fact]
    public void Armed_FiresAgainOnTheNextLap()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase lap1 = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Assert.Single(Fly(lap1, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm));

        DownwindPhase lap2 = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        (double _, PilotTransmission tx) = Assert.Single(Fly(lap2, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm));
        Assert.Equal("midfield left downwind runway 28R.", tx.Text);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Armed_Fires_UnderLandingClearance_AutoClearedToLand_AndOnAnExtendedDownwind(bool cleared, bool autoCleared, bool extended)
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        if (cleared)
        {
            ac.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        }

        PhaseContext ctx = Ctx(ac, autoClearedToLand: autoCleared);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended, AfterCrosswind(wp));
        (double _, PilotTransmission tx) = Assert.Single(Fly(phase, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm));
        Assert.Equal("midfield left downwind runway 28R.", tx.Text);
    }

    [Fact]
    public void Armed_ReplacesTheUnclearedMidfieldReminder()
    {
        // Uncleared solo VFR: unarmed, this leg voices the "midfield downwind" reminder. Armed, only the report is heard.
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        (double _, PilotTransmission tx) = Assert.Single(Fly(phase, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm));
        Assert.Equal("midfield left downwind runway 28R.", tx.Text);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void ArmedLate_PastMidfield_ReportsPastMidfieldAtOnce_AndStaysArmed()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Phases!.LandingClearance = ClearanceType.ClearedToLand; // no reminder on this leg
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);
        double pastMidfield = (midfield + DownwindPhase.MidfieldReportBandNm + AbeamAlongTrack(wp)) / 2.0;

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Assert.Empty(Fly(phase, ac, ctx, wp, pastMidfield));

        ac.Approach.ReportArmedMidfield = true;
        phase.OnTick(ctx);

        PilotTransmission tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("left downwind runway 28R, past midfield.", tx.Text);
        Assert.Contains("left downwind runway two eight right, past midfield", tx.SpeechText, StringComparison.OrdinalIgnoreCase);

        phase.OnTick(ctx);
        Assert.Single(ac.PendingPilotTransmissions);
        Assert.True(ac.Approach.ReportArmedMidfield);
    }

    /// <summary>
    /// The unarmed reminder already voiced on this leg does not stand for a report armed after it: the report follows,
    /// worded by position.
    /// </summary>
    [Fact]
    public void ArmedLate_AfterTheUnclearedReminder_StillReports()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);
        double pastMidfield = (midfield + DownwindPhase.MidfieldReportBandNm + AbeamAlongTrack(wp)) / 2.0;

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        (double _, PilotTransmission reminder) = Assert.Single(Fly(phase, ac, ctx, wp, pastMidfield));
        Assert.Contains("midfield downwind runway 28R", reminder.Text, StringComparison.Ordinal);

        ac.Approach.ReportArmedMidfield = true;
        phase.OnTick(ctx);

        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Equal("left downwind runway 28R, past midfield.", ac.PendingPilotTransmissions[1].Text);
    }

    [Fact]
    public void ArmedLate_PastAbeam_ReportsAbeamTheNumbers()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, AbeamAlongTrack(wp) + StepNm, extended: false, AfterCrosswind(wp));
        Assert.Empty(ac.PendingPilotTransmissions);

        ac.Approach.ReportArmedMidfield = true;
        phase.OnTick(ctx);

        PilotTransmission tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("left downwind runway 28R, abeam the numbers.", tx.Text);
    }

    [Fact]
    public void JoinedPastMidfield_WithDownwindAlsoArmed_GivesOneTransmissionAtDownwindStart()
    {
        // A 45-degree downwind entry joins at the abeam point (PatternCommandHandler.GetEntryPoint).
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        ac.Approach.ReportArmedDownwind = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, AbeamAlongTrack(wp), extended: false, AfterFortyFiveEntry(wp));

        PilotTransmission tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("left downwind runway 28R, abeam the numbers.", tx.Text);

        Fly(phase, ac, ctx, wp, AbeamAlongTrack(wp) + 0.2);
        Assert.Single(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void JoinedAtMidfield_WithDownwindAlsoArmed_GivesOneMidfieldTransmission()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        ac.Approach.ReportArmedDownwind = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase phase = StartDownwind(
            ac,
            ctx,
            wp,
            PatternGeometry.MidfieldAlongTrackNm(wp),
            extended: false,
            new MidfieldCrossingPhase { Waypoints = wp }
        );
        Fly(phase, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm);

        PilotTransmission tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("midfield left downwind runway 28R.", tx.Text);
    }

    /// <summary>
    /// A midfield-crossing join flown in from the departure-end side, 45 degrees off the downwind heading, hands over to the
    /// downwind 0.5 nm·cos 45° short of midfield: short of the report's trigger, but one transmission still answers both
    /// armed reports for the join.
    /// </summary>
    [Fact]
    public void MidfieldCrossingJoin_EndingShortOfMidfield_WithDownwindAlsoArmed_GivesOneMidfieldTransmission()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        ac.Approach.ReportArmedDownwind = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double trigger = PatternGeometry.MidfieldAlongTrackNm(wp) - DownwindPhase.MidfieldLeadNm;

        // Left traffic lies to the right of the downwind heading: in from the field, toward the pattern side.
        LatLon target = MidfieldCrossingPhase.MidfieldTarget(wp);
        TrueHeading track = wp.DownwindHeading + 45.0;
        LatLon start = GeoMath.ProjectPoint(target, track.ToReciprocal(), 2.0);
        var crossing = new MidfieldCrossingPhase { Waypoints = wp };
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(crossing);
        ac.Phases.Add(downwind);
        ac.Position = start;
        ac.Phases.Start(ctx);

        double flown = 0;
        while (!crossing.OnTick(ctx))
        {
            flown += StepNm;
            Assert.True(flown < 3.0, "the crossing never reached its target");
            ac.Position = GeoMath.ProjectPoint(start, track, flown);
        }

        ac.Phases.AdvanceToNext(ctx);
        Assert.Same(downwind, ac.Phases.CurrentPhase);
        double joinedAt = AlongTrack(wp, ac.Position);
        output.WriteLine($"downwind joined at along-track {joinedAt:F3}, trigger {trigger:F3}");
        Assert.True(joinedAt < trigger, "the crossing must hand over short of the midfield trigger");

        Fly(downwind, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm);

        PilotTransmission tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("midfield left downwind runway 28R.", tx.Text);
    }

    /// <summary>
    /// A downwind after a crosswind is not a join: even starting within a crossing's arrival distance of the trigger, it
    /// says "turning downwind" at the leg start and the midfield report later.
    /// </summary>
    [Fact]
    public void CrosswindToDownwind_WithDownwindAlsoArmed_VoicesBothAtTheirOwnPoints()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        ac.Approach.ReportArmedDownwind = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double trigger = PatternGeometry.MidfieldAlongTrackNm(wp) - DownwindPhase.MidfieldLeadNm;

        DownwindPhase phase = StartDownwind(ac, ctx, wp, trigger - (MidfieldCrossingPhase.ArrivalNm / 2.0), extended: false, AfterCrosswind(wp));
        Fly(phase, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm);

        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
        Assert.Equal("turning downwind runway 28R.", ac.PendingPilotTransmissions[0].Text);
        Assert.Equal("midfield left downwind runway 28R.", ac.PendingPilotTransmissions[1].Text);
    }

    [Fact]
    public void RightTraffic_SaysRight()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Right);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        (double _, PilotTransmission tx) = Assert.Single(Fly(phase, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm));
        Assert.Equal("midfield right downwind runway 28R.", tx.Text);
    }

    [Fact]
    public void Latch_SurvivesSnapshotRoundTrip()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Assert.Single(Fly(phase, ac, ctx, wp, midfield + StepNm));

        var dto = (DownwindPhaseDto)phase.ToSnapshot();
        Assert.True(dto.MidfieldReportIssued);
        var restored = DownwindPhase.FromSnapshot(dto);
        restored.OnTick(ctx);

        Assert.Single(ac.PendingPilotTransmissions);
    }

    /// <summary>
    /// A snapshot written before the midfield report existed carries neither the armed flag nor the leg's latch: restored,
    /// the report is not armed and the latch is clear.
    /// </summary>
    [Fact]
    public void Restore_FromSnapshotJsonWithoutTheMidfieldFields_IsNotArmed_AndTheLatchIsClear()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Fly(phase, ac, ctx, wp, PatternGeometry.MidfieldAlongTrackNm(wp) + StepNm);
        Assert.True(phase.MidfieldReportIssued);

        JsonObject approachJson = JsonSerializer.SerializeToNode(ac.Approach.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        JsonObject phaseJson = JsonSerializer.SerializeToNode(phase.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        Assert.True(approachJson.Remove(nameof(AircraftApproachStateDto.ReportArmedMidfield)));
        Assert.True(phaseJson.Remove(nameof(DownwindPhaseDto.MidfieldReportIssued)));

        AircraftApproachStateDto? approachDto = approachJson.Deserialize<AircraftApproachStateDto>(RecordingJsonOptions.Default);
        PhaseDto? phaseDto = phaseJson.Deserialize<PhaseDto>(RecordingJsonOptions.Default);
        Assert.NotNull(approachDto);
        DownwindPhaseDto downwindDto = Assert.IsType<DownwindPhaseDto>(phaseDto);

        Assert.False(AircraftApproachState.FromSnapshot(approachDto).ReportArmedMidfield);
        Assert.False(DownwindPhase.FromSnapshot(downwindDto).MidfieldReportIssued);
    }

    /// <summary>
    /// A 360 on the downwind after the report resumes a fresh copy of the leg; the copy keeps this lap's latch, so the
    /// report (and the reminder it stands for) is not voiced again on the way to the base turn.
    /// </summary>
    [Fact]
    public void ThreeSixtyOnDownwind_AfterTheReport_DoesNotRepeatIt()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        ac.Phases!.Add(new BasePhase { Waypoints = wp });
        Fly(phase, ac, ctx, wp, midfield + StepNm);
        Assert.Single(DownwindCalls(ac));

        CommandResult result = PatternCommandHandler.TryMakeTurn(ac, TurnDirection.Left, 360);
        Assert.True(result.Success, result.Message);
        Assert.IsType<MakeTurnPhase>(ac.Phases.CurrentPhase);

        // The turn is flown; the aircraft resumes the downwind where it began the turn.
        ac.Phases.AdvanceToNext(ctx);
        DownwindPhase resumed = Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);
        Assert.NotSame(phase, resumed);
        Fly(resumed, ac, ctx, wp, double.MaxValue);

        string call = Assert.Single(DownwindCalls(ac));
        Assert.Equal("midfield left downwind runway 28R.", call);
    }

    /// <summary>
    /// <c>MLT</c> re-issued on the downwind rebuilds the circuit from a fresh downwind on the same runway; that downwind
    /// keeps this lap's latch, so the report is not voiced again.
    /// </summary>
    [Fact]
    public void SameRunwayRebuildOnDownwind_AfterTheReport_DoesNotRepeatIt()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Fly(phase, ac, ctx, wp, midfield + StepNm);
        Assert.Single(DownwindCalls(ac));

        CommandResult result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left, runwayId: null, altitudeOverride: null);
        Assert.True(result.Success, result.Message);
        DownwindPhase rebuilt = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        Assert.NotSame(phase, rebuilt);
        Fly(rebuilt, ac, ctx, rebuilt.Waypoints!, double.MaxValue);

        string call = Assert.Single(DownwindCalls(ac));
        Assert.Equal("midfield left downwind runway 28R.", call);
    }

    /// <summary>
    /// <c>MLT 28L</c> from the 28R left downwind after the report rejoins 28L's downwind on the same side; that is a new
    /// downwind, and the aircraft, already past its midfield, reports once more with the new runway.
    /// </summary>
    [Fact]
    public void SwitchToTheParallelsDownwind_PastItsMidfield_ReportsOnceWithTheNewRunway()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        PatternWaypoints wp28L = Waypoints(Runway("28L"), PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Fly(phase, ac, ctx, wp, midfield + StepNm);
        Assert.Single(DownwindCalls(ac));

        // On along the 28R downwind until past 28L's midfield trigger.
        double trigger28L = PatternGeometry.MidfieldAlongTrackNm(wp28L) - DownwindPhase.MidfieldLeadNm;
        double along = AlongTrack(wp, ac.Position);
        while (AlongTrack(wp28L, ac.Position) < trigger28L + StepNm)
        {
            along += StepNm;
            Assert.True(along < AbeamAlongTrack(wp) + 1.0, "the 28R downwind never passed 28L's midfield");
            ac.Position = PointAtAlongTrack(wp, along);
        }

        CommandResult result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left, runwayId: "28L", altitudeOverride: null);
        Assert.True(result.Success, result.Message);
        DownwindPhase rebuilt = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        Fly(rebuilt, ac, ctx, rebuilt.Waypoints!, double.MaxValue);

        List<string> calls = DownwindCalls(ac);
        output.WriteLine(string.Join(Environment.NewLine, calls));
        Assert.Equal(2, calls.Count);
        Assert.Single(calls, c => c.Contains("runway 28R", StringComparison.Ordinal));
        Assert.Single(calls, c => c.Contains("runway 28L", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>ELD 28R</c> issued on the 28R left downwind after the report re-enters the same downwind; it keeps this lap's
    /// latch, so the report is not voiced again.
    /// </summary>
    [Fact]
    public void ReEnteringTheSameDownwind_AfterTheReport_DoesNotRepeatIt()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Fly(phase, ac, ctx, wp, midfield + StepNm);
        Assert.Single(DownwindCalls(ac));

        CommandResult result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, "28R", null);
        Assert.True(result.Success, result.Message);
        DownwindPhase reentered = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        Assert.NotSame(phase, reentered);
        Fly(reentered, ac, ctx, reentered.Waypoints!, double.MaxValue);

        string call = Assert.Single(DownwindCalls(ac));
        Assert.Equal("midfield left downwind runway 28R.", call);
    }

    /// <summary>
    /// <c>EXT DOWNWIND</c> issued on base after the report rolls the aircraft back onto an extended downwind; that downwind
    /// takes the latch from the lap's downwind earlier in the list, so the report is not voiced again.
    /// </summary>
    [Fact]
    public void ExtendDownwindFromBase_AfterTheReport_DoesNotRepeatIt()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        ac.Phases!.Add(new BasePhase { Waypoints = wp });
        Fly(phase, ac, ctx, wp, double.MaxValue);
        Assert.Single(DownwindCalls(ac));
        ac.Phases.AdvanceToNext(ctx);
        Assert.IsType<BasePhase>(ac.Phases.CurrentPhase);

        CommandResult result = PatternCommandHandler.TryExtendPattern(ac, PatternEntryLeg.Downwind);
        Assert.True(result.Success, result.Message);
        DownwindPhase extended = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        Assert.True(extended.IsExtended);
        Fly(extended, ac, ctx, extended.Waypoints!, AlongTrack(extended.Waypoints!, ac.Position) + 0.5);

        string call = Assert.Single(DownwindCalls(ac));
        Assert.Equal("midfield left downwind runway 28R.", call);
    }

    /// <summary>
    /// <c>MRT</c> on the 28R left downwind after the report crosses the field to the right downwind: a new downwind on the
    /// other side, which reports once more.
    /// </summary>
    [Fact]
    public void WrongSideDirectionFlipOnDownwind_AfterTheReport_ReportsOnceOnTheNewSide()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(wp);

        DownwindPhase phase = StartDownwind(ac, ctx, wp, DownwindStartAlongTrack(wp), extended: false, AfterCrosswind(wp));
        Fly(phase, ac, ctx, wp, midfield + StepNm);
        Assert.Single(DownwindCalls(ac));

        CommandResult result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Right, runwayId: null, altitudeOverride: null);
        Assert.True(result.Success, result.Message);
        MidfieldCrossingPhase crossing = Assert.IsType<MidfieldCrossingPhase>(ac.Phases!.CurrentPhase);
        LatLon target = MidfieldCrossingPhase.MidfieldTarget(crossing.Waypoints!);
        double flown = 0;
        while (!crossing.OnTick(ctx))
        {
            flown += StepNm;
            Assert.True(flown < 5.0, "the crossing never reached its target");
            ac.Position = GeoMath.ProjectPoint(ac.Position, new TrueHeading(GeoMath.BearingTo(ac.Position, target)), StepNm);
        }

        ac.Phases.AdvanceToNext(ctx);
        DownwindPhase rightDownwind = Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(PatternDirection.Right, rightDownwind.Waypoints!.Direction);
        Fly(rightDownwind, ac, ctx, rightDownwind.Waypoints, double.MaxValue);

        List<string> calls = DownwindCalls(ac);
        output.WriteLine(string.Join(Environment.NewLine, calls));
        Assert.Equal(2, calls.Count);
        Assert.Single(calls, c => c.Contains("left downwind runway 28R", StringComparison.Ordinal));
        Assert.Single(calls, c => c.Contains("right downwind runway 28R", StringComparison.Ordinal));
    }

    /// <summary>
    /// A 360 issued 0.3 nm short of the trigger with both reports armed resumes a copy of the leg that is not a join: no
    /// early merge, so the midfield report comes at the trigger.
    /// </summary>
    [Fact]
    public void ThreeSixtyShortOfTheTrigger_WithDownwindAlsoArmed_ReportsMidfieldAtTheTrigger()
    {
        PatternWaypoints wp = Waypoints(PatternDirection.Left);
        AircraftState ac = MakeAircraft(wp);
        ac.Approach.ReportArmedMidfield = true;
        ac.Approach.ReportArmedDownwind = true;
        PhaseContext ctx = Ctx(ac, autoClearedToLand: false);
        double trigger = PatternGeometry.MidfieldAlongTrackNm(wp) - DownwindPhase.MidfieldLeadNm;

        StartDownwind(ac, ctx, wp, trigger - 0.3, extended: false, AfterCrosswind(wp));
        ac.Phases!.Add(new BasePhase { Waypoints = wp });

        CommandResult result = PatternCommandHandler.TryMakeTurn(ac, TurnDirection.Left, 360);
        Assert.True(result.Success, result.Message);
        ac.Phases.AdvanceToNext(ctx);
        DownwindPhase resumed = Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);
        Assert.DoesNotContain(DownwindCalls(ac), c => c.StartsWith("midfield", StringComparison.Ordinal));

        List<(double AlongTrack, PilotTransmission Tx)> heard = Fly(resumed, ac, ctx, wp, AbeamAlongTrack(wp) - StepNm);

        (double firedAt, PilotTransmission tx) = Assert.Single(heard, h => h.Tx.Text.StartsWith("midfield", StringComparison.Ordinal));
        Assert.Equal("midfield left downwind runway 28R.", tx.Text);
        Assert.InRange(firedAt, trigger, trigger + StepNm);
    }

    /// <summary>
    /// A crossover to the parallel (MLT 28L from the 28R right downwind, before midfield) flies the old downwind to its
    /// midfield and leaves it there; the report belongs to the downwind that leads to the landing, on 28L.
    /// Recording: S2-OAK-4, N342T on the 28R right downwind at t=980 (see <c>ParallelRunwayMltFromDownwindTests</c>).
    /// </summary>
    [Fact]
    public void Crossover_FiresOnTheLandingDownwind_WithThatRunway()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive("TestData/parallel-mlt-from-upwind-recording.yaat-bug-report-bundle.zip");
        if (archive is null)
        {
            Assert.Fail("missing fixture TestData/parallel-mlt-from-upwind-recording.yaat-bug-report-bundle.zip");
        }

        using (archive)
        {
            TestVnasData.EnsureInitialized();
            SimLogBuilder.CreateForTest(output).EnableCategory("DownwindPhase", LogLevel.Debug).InitializeSimLog();
            var engine = new SimulationEngine(RecordingGroundData.Evergreen("a midfield report across a crossover"), PushPlanStore.CreateShipped());
            engine.Replay(archive.ToBaseSessionRecording(), 0);
            TimedSnapshot? snapshot = archive.ReadSnapshotAt(980);
            if (snapshot is null)
            {
                Assert.Fail("the fixture recording has no snapshot at t=980");
            }

            engine.RestoreFromSnapshot(snapshot.State);
            AircraftState? aircraft = engine.FindAircraft("N342T");
            Assert.NotNull(aircraft);
            Assert.IsType<DownwindPhase>(aircraft.Phases?.CurrentPhase);

            var heard = new List<string>();
            engine.WarningEmitted += (callsign, warning) =>
            {
                if (callsign == "N342T")
                {
                    heard.Add(warning);
                }
            };
            engine.PilotSpeechEmitted += (callsign, text) =>
            {
                if (callsign == "N342T")
                {
                    heard.Add(text.TerminalForRpo);
                }
            };

            aircraft.Approach.ReportArmedMidfield = true;
            CommandResult result = PatternCommandHandler.TryChangePatternDirection(
                aircraft,
                PatternDirection.Left,
                "28L",
                null,
                aircraft.Ground.Layout
            );
            Assert.True(result.Success, result.Message);

            bool flewLandingDownwind = false;
            for (int i = 0; i < 300; i++)
            {
                engine.TickOneSecond();
                AircraftState? ac = engine.FindAircraft("N342T");
                if (ac?.Phases?.CurrentPhase is DownwindPhase { ExitAtMidfield: false })
                {
                    flewLandingDownwind = true;
                }
                else if (flewLandingDownwind)
                {
                    break;
                }
            }

            output.WriteLine(string.Join(Environment.NewLine, heard));
            Assert.True(flewLandingDownwind, "the aircraft never reached 28L's downwind");
            string report = Assert.Single(heard, h => h.Contains("downwind", StringComparison.OrdinalIgnoreCase));
            // The crossing hands over to 28L's downwind more than 0.1 nm past midfield, so the position wording governs.
            Assert.Equal("left downwind runway 28L, past midfield.", report);
        }
    }
}

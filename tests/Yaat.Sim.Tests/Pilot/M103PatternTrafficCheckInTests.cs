using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// M10.1.3: VFR closed-traffic pilot speech. Covers the initial-call hook on
/// <see cref="PatternEntryPhase"/>, the mode-branched midfield-downwind reminder on
/// <see cref="DownwindPhase"/>, and the mode-branched short-final reminder on
/// <see cref="FinalApproachPhase"/> (plus the M10.1.1 spawn-on-final suppression for
/// pattern traffic).
/// </summary>
public class M103PatternTrafficCheckInTests
{
    private static RunwayInfo DefaultRunway() =>
        TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 9, thresholdLat: 37.7212, thresholdLon: -122.2208);

    private static PatternWaypoints DefaultWaypoints(PatternDirection dir = PatternDirection.Left) =>
        PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, dir, null, null, null);

    private static AircraftState MakeAircraft(
        string callsign = "N123AB",
        bool isVfr = true,
        double altitude = 1500,
        double ias = 90,
        bool onGround = false
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = new LatLon(37.7212, -122.2208),
            TrueHeading = new TrueHeading(100),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = onGround,
            FlightPlan = new AircraftFlightPlan { FlightRules = isVfr ? "VFR" : "IFR", HasFlightPlan = true },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo? rwy = null, bool soloMode = true, bool autoClearedToLand = false, double dt = 1.0)
    {
        var runway = rwy ?? DefaultRunway();
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = dt,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            SoloTrainingMode = soloMode,
            AutoClearedToLand = autoClearedToLand,
        };
    }

    private static string? SinglePilotLine(AircraftState ac) => ac.PendingPilotTransmissions.SingleOrDefault()?.SpeechText;

    // ─────────────────────────────────────────────────────────────────────
    // PatternEntryPhase — initial closed-traffic request
    // ─────────────────────────────────────────────────────────────────────

    private static PatternEntryPhase MakePatternEntry(double entryLat = 37.71, double entryLon = -122.25, double altitude = 1100) =>
        new()
        {
            EntryLat = entryLat,
            EntryLon = entryLon,
            PatternAltitude = altitude,
            Kind = PatternEntryKind.FortyFive,
        };

    [Fact]
    public void PatternEntry_VfrFirstActivation_FiresInitialCallAndSetsFlag()
    {
        // 3 nm south of the runway threshold, 1500 ft.
        var ac = MakeAircraft(isVfr: true, altitude: 1500);
        ac.Position = GeoMath.ProjectPoint(new LatLon(37.7212, -122.2208), new TrueHeading(180), 3);

        var phase = MakePatternEntry();
        phase.OnStart(Ctx(ac));

        var line = SinglePilotLine(ac);
        Assert.NotNull(line);
        Assert.Contains("november one two three alpha bravo", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("three miles south at one thousand five hundred", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request closed traffic", line);
        Assert.Contains("with information Alpha", line);
        Assert.True(ac.HasMadeInitialContact);
    }

    [Fact]
    public void PatternEntry_AlreadyContacted_DoesNotFire()
    {
        // Aircraft that was already announced via M10.1.2 airborne-spawn check-in won't re-announce.
        var ac = MakeAircraft();
        ac.HasMadeInitialContact = true;

        var phase = MakePatternEntry();
        phase.OnStart(Ctx(ac));

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void PatternEntry_Ifr_DoesNotFire()
    {
        var ac = MakeAircraft(isVfr: false);
        var phase = MakePatternEntry();
        phase.OnStart(Ctx(ac));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void PatternEntry_SoloModeOff_DoesNotFire()
    {
        var ac = MakeAircraft();
        var phase = MakePatternEntry();
        phase.OnStart(Ctx(ac, soloMode: false));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void PatternEntry_RunwayNull_DoesNotFire()
    {
        // No runway in context → phase can't anchor distance/bearing → skips the call.
        var ac = MakeAircraft();
        var phase = MakePatternEntry();
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = null,
            FieldElevation = 0,
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
        };

        phase.OnStart(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void PatternEntry_SnapshotRoundTrip_PreservesAnnouncedFlag()
    {
        var ac = MakeAircraft();
        ac.Position = GeoMath.ProjectPoint(new LatLon(37.7212, -122.2208), new TrueHeading(180), 3);
        var phase = MakePatternEntry();
        phase.OnStart(Ctx(ac));

        var dto = (PatternEntryPhaseDto)phase.ToSnapshot();
        Assert.True(dto.HasAnnouncedInitialCall);

        var restored = PatternEntryPhase.FromSnapshot(dto);
        // Restored phase shouldn't re-fire on a second OnStart.
        var ac2 = MakeAircraft();
        ac2.Position = GeoMath.ProjectPoint(new LatLon(37.7212, -122.2208), new TrueHeading(180), 3);
        restored.OnStart(Ctx(ac2));

        Assert.Empty(ac2.PendingPilotTransmissions);
    }

    // ─────────────────────────────────────────────────────────────────────
    // DownwindPhase — midfield reminder, mode-branched
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Positions the aircraft at the downwind-abeam waypoint. By construction this is past the
    /// midfield-along-track trigger (midfield = _abeamAlongTrack / 2), so OnTick fires the
    /// midfield broadcast on the first call.
    /// </summary>
    private static (DownwindPhase phase, AircraftState ac, PhaseContext ctx) BuildDownwindAtMidfield(
        bool isVfr = true,
        bool soloMode = true,
        bool cleared = false,
        bool autoClearedToLand = false
    )
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(isVfr: isVfr, altitude: wp.PatternAltitude);
        ac.Position = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        ac.TrueHeading = wp.DownwindHeading;

        if (cleared)
        {
            ac.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        }

        var phase = new DownwindPhase { Waypoints = wp };
        var ctx = Ctx(ac, soloMode: soloMode, autoClearedToLand: autoClearedToLand);
        phase.OnStart(ctx);
        return (phase, ac, ctx);
    }

    [Fact]
    public void Downwind_SoloVfrUnclearedAtMidfield_FiresPilotSpeech()
    {
        var (phase, ac, ctx) = BuildDownwindAtMidfield();
        phase.OnTick(ctx);

        var line = SinglePilotLine(ac);
        Assert.NotNull(line);
        Assert.Contains("midfield downwind runway two eight right", line, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Downwind_RpoModeUnclearedAtMidfield_FiresControllerWarning()
    {
        var (phase, ac, ctx) = BuildDownwindAtMidfield(soloMode: false);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        var warning = ac.PendingWarnings.SingleOrDefault();
        Assert.NotNull(warning);
        Assert.Contains("midfield downwind runway 28R", warning);
    }

    [Fact]
    public void Downwind_IfrAtMidfield_FiresControllerWarningEvenInSoloMode()
    {
        // IFR pattern aircraft don't speak in solo mode (gated by IsVfr) — falls through to the warning channel.
        var (phase, ac, ctx) = BuildDownwindAtMidfield(isVfr: false);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.NotEmpty(ac.PendingWarnings);
    }

    [Fact]
    public void Downwind_ClearedAtMidfield_FiresNothing()
    {
        var (phase, ac, ctx) = BuildDownwindAtMidfield(cleared: true);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Downwind_AutoClearedToLand_FiresNothing()
    {
        var (phase, ac, ctx) = BuildDownwindAtMidfield(autoClearedToLand: true);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Downwind_FiresOnceWithinSamePhaseInstance()
    {
        var (phase, ac, ctx) = BuildDownwindAtMidfield();
        phase.OnTick(ctx);
        phase.OnTick(ctx);
        phase.OnTick(ctx);

        Assert.Single(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void Downwind_FreshInstancePerLap_ReFires()
    {
        // First lap.
        var (phase1, ac, ctx1) = BuildDownwindAtMidfield();
        phase1.OnTick(ctx1);
        Assert.Single(ac.PendingPilotTransmissions);

        // Second lap: fresh DownwindPhase instance built by PatternBuilder.BuildNextCircuit().
        var wp = DefaultWaypoints();
        ac.Position = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        ac.TrueHeading = wp.DownwindHeading;
        ac.Phases!.LandingClearance = null; // still uncleared on lap 2

        var phase2 = new DownwindPhase { Waypoints = wp };
        var ctx2 = Ctx(ac);
        phase2.OnStart(ctx2);
        phase2.OnTick(ctx2);

        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
    }

    // ─────────────────────────────────────────────────────────────────────
    // FinalApproachPhase — short-final reminder, mode-branched + M10.1.1 suppression
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Positions the aircraft 0.8 NM from the threshold on a 280° final, IsPatternTraffic
    /// optional, no landing clearance. Returns the phase ready for OnTick.
    /// </summary>
    private static (FinalApproachPhase phase, AircraftState ac, PhaseContext ctx) BuildShortFinal(
        bool isVfr = true,
        bool soloMode = true,
        bool isPatternTraffic = true,
        bool cleared = false,
        double distNm = 0.8
    )
    {
        var ac = MakeAircraft(callsign: "N123AB", isVfr: isVfr, altitude: 300, ias: 75);
        // Already announced upstream (e.g., via PatternEntryPhase or M10.1.2) — short-final is a reminder, not initial contact.
        ac.HasMadeInitialContact = true;

        var rwy = DefaultRunway();
        // Distance from threshold along reversed-runway direction (i.e., on final, approaching from upwind direction = 100° → aircraft is east of threshold? No.
        // For runway 28 (heading 280°), the approach end is on the east side; aircraft on final approaches FROM the east heading 280°.
        // Aircraft 0.8 nm from threshold on final = 0.8 nm to the east of threshold (along 100° from threshold).
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), new TrueHeading(100), distNm);
        ac.TrueHeading = new TrueHeading(280);

        if (isPatternTraffic)
        {
            ac.Phases!.TrafficDirection = PatternDirection.Left;
        }

        if (cleared)
        {
            ac.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        }

        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = Ctx(ac, rwy, soloMode: soloMode);
        phase.OnStart(ctx);
        return (phase, ac, ctx);
    }

    [Fact]
    public void FinalApproach_SoloVfrPatternUnclearedAtShortFinal_FiresPilotSpeech()
    {
        var (phase, ac, ctx) = BuildShortFinal();
        // Pattern traffic with HasMadeInitialContact already true → M10.1.1 OnFinal does NOT fire on OnStart.
        Assert.Empty(ac.PendingPilotTransmissions);

        phase.OnTick(ctx);

        var line = SinglePilotLine(ac);
        Assert.NotNull(line);
        Assert.Contains("short final runway two eight right", line, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void FinalApproach_RpoModePatternUncleared_FiresControllerWarning()
    {
        var (phase, ac, ctx) = BuildShortFinal(soloMode: false);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        var warning = ac.PendingWarnings.SingleOrDefault();
        Assert.NotNull(warning);
        // RPO-default warning now shows the pilot's compact terminal line (callsign in the SAY column).
        Assert.Contains("short final runway 28R", warning);
    }

    [Fact]
    public void FinalApproach_NonPatternVfrUncleared_FiresControllerWarningNotPilotSpeech()
    {
        // VFR aircraft on straight-in arrival (not pattern traffic) — pilot speech is gated by _isPatternTraffic.
        // Falls through to the existing warning channel.
        var (phase, ac, ctx) = BuildShortFinal(isPatternTraffic: false);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.NotEmpty(ac.PendingWarnings);
    }

    [Fact]
    public void FinalApproach_IfrPatternUncleared_FiresControllerWarningNotPilotSpeech()
    {
        // IFR pattern aircraft — gated by IsVfr; falls through to warning.
        var (phase, ac, ctx) = BuildShortFinal(isVfr: false);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.NotEmpty(ac.PendingWarnings);
    }

    [Fact]
    public void FinalApproach_PatternCleared_FiresNothing()
    {
        var (phase, ac, ctx) = BuildShortFinal(cleared: true);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void FinalApproach_PatternBeyondOneNm_DoesNotFire()
    {
        // 2 NM out — beyond the NoClearanceWarningDistNm = 1.0 trigger.
        var (phase, ac, ctx) = BuildShortFinal(distNm: 2.0);
        phase.OnTick(ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void FinalApproach_PatternTrafficSpawnFresh_M101OnFinalDoesNotFire()
    {
        // Defense-in-depth: even if HasMadeInitialContact is somehow false, pattern traffic
        // should never trigger the M10.1.1 spawn-on-final speech (PatternEntryPhase owns the
        // initial call for pattern aircraft).
        var ac = MakeAircraft(callsign: "N123AB", isVfr: true, altitude: 1000, ias: 80);
        ac.HasMadeInitialContact = false;
        var rwy = DefaultRunway();
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), new TrueHeading(100), 3);
        ac.TrueHeading = new TrueHeading(280);
        ac.Phases!.TrafficDirection = PatternDirection.Left;

        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = Ctx(ac, rwy, soloMode: true);
        phase.OnStart(ctx);

        // M10.1.1 OnStart spawn-on-final speech should NOT fire for pattern traffic.
        Assert.Empty(ac.PendingPilotTransmissions);
    }
}

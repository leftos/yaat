using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Phase-entry tests for M10.1.1: ground spawn check-ins fire for both IFR and VFR aircraft
/// across AtParking, HoldingShort, LinedUp, and FinalApproach (spawn-on-final) phases.
/// </summary>
public class M101GroundSpawnCheckInTests
{
    private static AircraftState MakeAircraft(string callsign = "TEST1", bool isVfr = false, bool hasFlightPlan = true, string? parkingSpot = null)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(280),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { FlightRules = isVfr ? "VFR" : "IFR", HasFlightPlan = hasFlightPlan },
            Ground = new AircraftGroundOps { ParkingSpot = parkingSpot },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo? rwy = null, bool soloMode = true, double dt = 1.0) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy?.ElevationFt ?? 0,
            Logger = NullLogger.Instance,
            SoloTrainingMode = soloMode,
        };

    private static void TickElapsed(Phase phase, PhaseContext ctx, double seconds)
    {
        // Phase tracks its own ElapsedSeconds via the base ticker; here we manipulate it directly.
        phase.ElapsedSeconds = seconds;
        phase.OnTick(ctx);
    }

    // --- AtParkingPhase ---

    [Fact]
    public void AtParking_VfrNoFlightPlan_FiresCheckInAfter5s()
    {
        // Regression for M10.1.1's dropped HasFlightPlan gate: VFR aircraft also call ground.
        var ac = MakeAircraft("N123AB", isVfr: true, hasFlightPlan: false, parkingSpot: "KILO RAMP");
        var phase = new AtParkingPhase();
        var ctx = Ctx(ac);

        phase.OnStart(ctx);
        TickElapsed(phase, ctx, 5.0);

        Assert.Single(ac.PendingNotifications);
        Assert.Contains("november one two three alpha bravo at kilo ramp", ac.PendingNotifications[0]);
        Assert.Contains("with information Alpha", ac.PendingNotifications[0]);
        Assert.True(ac.HasMadeInitialContact);
        Assert.True(ac.Ground.HasAnnouncedReady);
    }

    [Fact]
    public void AtParking_BeforeDelay_DoesNotFire()
    {
        var ac = MakeAircraft();
        var phase = new AtParkingPhase();
        var ctx = Ctx(ac);

        phase.OnStart(ctx);
        TickElapsed(phase, ctx, 4.9);

        Assert.Empty(ac.PendingNotifications);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void AtParking_FiresOnceOnly()
    {
        var ac = MakeAircraft();
        var phase = new AtParkingPhase();
        var ctx = Ctx(ac);

        phase.OnStart(ctx);
        TickElapsed(phase, ctx, 5.0);
        TickElapsed(phase, ctx, 6.0);
        TickElapsed(phase, ctx, 10.0);

        Assert.Single(ac.PendingNotifications);
    }

    [Fact]
    public void AtParking_SoloModeOff_DoesNotFire()
    {
        var ac = MakeAircraft();
        var phase = new AtParkingPhase();
        var ctx = Ctx(ac, soloMode: false);

        phase.OnStart(ctx);
        TickElapsed(phase, ctx, 10.0);

        Assert.Empty(ac.PendingNotifications);
        Assert.False(ac.HasMadeInitialContact);
    }

    // --- HoldingShortPhase ---

    [Fact]
    public void HoldingShort_RunwayCrossing_FiresPilotCheckIn()
    {
        var ac = MakeAircraft();
        var holdShort = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28R",
        };
        var phase = new HoldingShortPhase(holdShort);
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        var pilotLine = ac.PendingNotifications.SingleOrDefault();
        Assert.NotNull(pilotLine);
        Assert.Contains("holding short runway two eight right", pilotLine);
        Assert.Contains("ready for departure", pilotLine);
        Assert.True(ac.HasMadeInitialContact);
    }

    [Fact]
    public void HoldingShort_ExplicitHoldShort_DoesNotFirePilotCheckIn()
    {
        var ac = MakeAircraft();
        var holdShort = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "28R",
        };
        var phase = new HoldingShortPhase(holdShort);
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Sim warning still fires; pilot check-in does NOT.
        Assert.Empty(ac.PendingNotifications);
        Assert.NotEmpty(ac.PendingWarnings);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void HoldingShort_PerInstanceFlag_ReFiresOnNewPhaseEntry()
    {
        var ac = MakeAircraft();

        var phase1 = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 1,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = "28L",
            }
        );
        phase1.OnStart(Ctx(ac));
        Assert.Single(ac.PendingNotifications);

        // Different hold-short location → different phase instance → re-fires.
        var phase2 = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 2,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = "28R",
            }
        );
        phase2.OnStart(Ctx(ac));

        Assert.Equal(2, ac.PendingNotifications.Count);
        Assert.Contains("holding short runway two eight left", ac.PendingNotifications[0]);
        Assert.Contains("holding short runway two eight right", ac.PendingNotifications[1]);
    }

    [Fact]
    public void HoldingShort_SoloModeOff_DoesNotFirePilotCheckIn()
    {
        var ac = MakeAircraft();
        var holdShort = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28R",
        };
        var phase = new HoldingShortPhase(holdShort);
        var ctx = Ctx(ac, soloMode: false);

        phase.OnStart(ctx);

        Assert.Empty(ac.PendingNotifications);
    }

    // --- LinedUpAndWaitingPhase ---

    [Fact]
    public void LinedUp_BeforeReminderDelay_DoesNotFire()
    {
        var ac = MakeAircraft();
        var rwy = TestRunwayFactory.Make(designator: "28R");
        var phase = new LinedUpAndWaitingPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);
        TickElapsed(phase, ctx, 9.9);

        Assert.Empty(ac.PendingNotifications);
    }

    [Fact]
    public void LinedUp_PastDelayNoClearance_FiresReminderOnce()
    {
        var ac = MakeAircraft();
        var rwy = TestRunwayFactory.Make(designator: "28R");
        var phase = new LinedUpAndWaitingPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);
        TickElapsed(phase, ctx, 10.0);

        Assert.Single(ac.PendingNotifications);
        Assert.Contains("runway two eight right, ready", ac.PendingNotifications[0]);
        Assert.True(ac.HasAnnouncedLinedUpReady);
        Assert.True(ac.HasMadeInitialContact);

        // Subsequent ticks must not re-fire.
        TickElapsed(phase, ctx, 11.0);
        TickElapsed(phase, ctx, 30.0);
        Assert.Single(ac.PendingNotifications);
    }

    [Fact]
    public void LinedUp_WithDepartureClearance_NeverFires()
    {
        var ac = MakeAircraft();
        ac.Phases!.DepartureClearance = new DepartureClearanceInfo { Type = ClearanceType.ClearedForTakeoff, Departure = new DefaultDeparture() };
        var rwy = TestRunwayFactory.Make(designator: "28R");
        var phase = new LinedUpAndWaitingPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);
        TickElapsed(phase, ctx, 30.0);

        Assert.Empty(ac.PendingNotifications);
        Assert.False(ac.HasAnnouncedLinedUpReady);
    }

    // --- FinalApproachPhase (spawn-on-final OnFinal check-in) ---

    [Fact]
    public void FinalApproach_SpawnOnFinalIfrWithApproach_FiresIfrBranch()
    {
        var ac = MakeAircraft("AAL123");
        ac.IsOnGround = false;
        ac.Position = new LatLon(37.04, -122.0); // ~3 nm short of runway threshold
        ac.Altitude = 1000;
        ac.IndicatedAirspeed = 140;
        ac.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "KTEST",
            RunwayId = "28R",
            FinalApproachCourse = new TrueHeading(280),
        };

        var rwy = TestRunwayFactory.Make(designator: "28R");
        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        var line = ac.PendingNotifications.SingleOrDefault();
        Assert.NotNull(line);
        Assert.Contains("ILS two eight right", line);
        Assert.True(ac.HasMadeInitialContact);
    }

    [Fact]
    public void FinalApproach_SpawnOnFinalVfr_FiresVfrBranchWithDistance()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, hasFlightPlan: false);
        ac.IsOnGround = false;
        // ~3 nm short on a 280° final.
        ac.Position = new LatLon(37.0, -122.05);
        ac.Altitude = 1000;
        ac.IndicatedAirspeed = 80;

        var rwy = TestRunwayFactory.Make(designator: "28R");
        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        var line = ac.PendingNotifications.SingleOrDefault();
        Assert.NotNull(line);
        Assert.Contains("final runway two eight right", line);
        Assert.Contains("with information Alpha", line);
        Assert.True(ac.HasMadeInitialContact);
    }

    [Fact]
    public void FinalApproach_HasMadeInitialContact_DoesNotFire()
    {
        // Aircraft that flew the approach (was already talking) doesn't re-announce on final.
        var ac = MakeAircraft("AAL123");
        ac.IsOnGround = false;
        ac.HasMadeInitialContact = true;
        ac.Position = new LatLon(37.04, -122.0);
        ac.Altitude = 1000;
        ac.IndicatedAirspeed = 140;

        var rwy = TestRunwayFactory.Make(designator: "28R");
        var phase = new FinalApproachPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        Assert.Empty(ac.PendingNotifications);
    }
}

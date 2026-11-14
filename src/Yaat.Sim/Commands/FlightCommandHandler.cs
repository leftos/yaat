using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

internal static class FlightCommandHandler
{
    /// <summary>Space-joined fix list for command responses, each fix as "NAME (ID)" when it has
    /// an authored display name (e.g. "LAKE CHABOT (VPCBT)"), or the bare identifier otherwise.</summary>
    private static string FixListSpaced(IEnumerable<ResolvedFix> fixes) =>
        string.Join(" ", fixes.Select(f => PhraseologyVerbalizer.FixDisplayTextUpper(f.Name)));

    /// <summary>Comma-joined variant of <see cref="FixListSpaced"/> for "not programmed" warnings.</summary>
    private static string FixListCommas(IEnumerable<ResolvedFix> fixes) =>
        string.Join(", ", fixes.Select(f => PhraseologyVerbalizer.FixDisplayTextUpper(f.Name)));

    /// <summary>
    /// Minimum speed (KIAS) ATC may assign a helicopter via a radar speed instruction
    /// (7110.65 §5-7-3.e: "assign a speed not less than 60 knots"). A plain SPD below this is
    /// floored to it; a forced assignment (SPEEDF, or the SPEEDN / SPDN / SLN teleport) bypasses
    /// the floor and may command any speed (the §5-7-3.f "operationally advantageous" exception).
    /// </summary>
    private const int HelicopterMinRadarSpeedKts = 60;

    internal static CommandResult ApplyHeading(FlyHeadingCommand cmd, AircraftState aircraft)
    {
        // A cardinal heading is a VFR instruction ("proceed eastbound"); an IFR aircraft is vectored with the
        // numeric form, so the refusal names the heading that would fly the same.
        if ((cmd.Cardinal is { } cardinal) && !aircraft.FlightPlan.IsVfr)
        {
            return new CommandResult(false, $"FH {cardinal} is VFR only; use FH {cmd.MagneticHeading.ToDisplayString()}");
        }

        string prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        string message = cmd.Cardinal is { } direction
            ? $"Proceed {CommandDescriber.DirectionWord(direction)} (heading {cmd.MagneticHeading.ToDisplayString()})"
            : $"Fly heading {cmd.MagneticHeading.Degrees:000}{prev}";
        return CommandDispatcher.Ok(message);
    }

    internal static CommandResult ApplyTurnLeft(TurnLeftCommand cmd, AircraftState aircraft)
    {
        string prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
        return CommandDispatcher.Ok($"Turn left heading {cmd.MagneticHeading.Degrees:000}{prev}");
    }

    internal static CommandResult ApplyTurnRight(TurnRightCommand cmd, AircraftState aircraft)
    {
        string prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
        return CommandDispatcher.Ok($"Turn right heading {cmd.MagneticHeading.Degrees:000}{prev}");
    }

    internal static CommandResult ApplyLeftTurn(LeftTurnCommand cmd, AircraftState aircraft)
    {
        string prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading - cmd.Degrees;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading - cmd.Degrees;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
        int leftHdg = (aircraft.MagneticHeading - cmd.Degrees).ToDisplayInt();
        return CommandDispatcher.Ok($"Turn {cmd.Degrees} degrees left, heading {leftHdg:000}{prev}");
    }

    internal static CommandResult ApplyRightTurn(RightTurnCommand cmd, AircraftState aircraft)
    {
        string prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading + cmd.Degrees;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading + cmd.Degrees;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
        int rightHdg = (aircraft.MagneticHeading + cmd.Degrees).ToDisplayInt();
        return CommandDispatcher.Ok($"Turn {cmd.Degrees} degrees right, heading {rightHdg:000}{prev}");
    }

    internal static CommandResult ApplyFlyPresentHeading(AircraftState aircraft)
    {
        string prev = PreviousLateralGuidance(aircraft);
        int hdg = aircraft.MagneticHeading.ToDisplayInt();
        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        return CommandDispatcher.Ok($"Fly present heading {hdg:000}{prev}");
    }

    internal static CommandResult ApplyForceHeading(ForceHeadingCommand cmd, AircraftState aircraft)
    {
        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.TrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.TrueTrack = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        return CommandDispatcher.Ok($"Force heading {cmd.MagneticHeading.Degrees:000}");
    }

    internal static CommandResult ApplyClimbMaintain(ClimbMaintainCommand cmd, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (cmd.Modifier != AltitudeAssignmentModifier.None)
        {
            return ApplyVfrAltitudeRestriction(cmd, aircraft);
        }

        string prev = PreviousAltitude(aircraft, cmd.Altitude);
        CancelViaClearances(aircraft);
        aircraft.Procedure.IsExpediting = false;
        aircraft.Targets.TargetAltitude = cmd.Altitude;
        aircraft.Targets.AltitudeFloor = null;
        aircraft.Targets.AltitudeCeiling = null;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;
        SetPatternAltitudeOverride(aircraft, cmd.Altitude, groundLayout);

        return CommandDispatcher.Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}{prev}");
    }

    /// <summary>
    /// A new altitude clearance (CM, DM, a VFR altitude restriction, PTAC, force altitude) cancels any
    /// climb via or descend via, whichever direction (7110.65 §4-5-7.h.11 NOTE 1): the new altitude
    /// replaces the procedure's altitude restrictions.
    /// </summary>
    internal static void CancelViaClearances(AircraftState aircraft)
    {
        aircraft.Procedure.SidViaMode = false;
        aircraft.Procedure.ClimbViaTopFromTdls = false;
        aircraft.Procedure.SidViaCeiling = null;
        aircraft.Procedure.ClimbViaTargetFt = null;
        aircraft.Procedure.ClimbViaTransmissionId = null;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.StarViaFloor = null;
    }

    /// <summary>
    /// An altitude assignment is the most recent one (AIM 4-4-10.a), so it replaces any altitude an option clearance deferred
    /// to the next runway's pattern (<see cref="PhaseList.TransitionPatternAltitude"/>) wherever the aircraft is — a follower
    /// in pursuit included. Assigned in the pattern, it also becomes the standing pattern altitude.
    /// </summary>
    private static void SetPatternAltitudeOverride(AircraftState aircraft, int altitude, AirportGroundLayout? groundLayout)
    {
        if (aircraft.Phases is not { } phases)
        {
            return;
        }

        phases.TransitionPatternAltitude = null;
        if (phases.TrafficDirection is not null)
        {
            aircraft.Pattern.SetAltitudeOverride(altitude, phases, AircraftCategorization.Categorize(aircraft.AircraftType));
            PatternCommandHandler.RefreshPatternWaypointsForAltitude(aircraft, groundLayout);
        }
    }

    private static CommandResult ApplyVfrAltitudeRestriction(ClimbMaintainCommand cmd, AircraftState aircraft)
    {
        CancelViaClearances(aircraft);
        aircraft.Procedure.IsExpediting = false;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;

        switch (cmd.Modifier)
        {
            case AltitudeAssignmentModifier.AtOrAbove:
                aircraft.Targets.AltitudeFloor = cmd.Altitude;
                aircraft.Targets.AltitudeCeiling = null;
                aircraft.Targets.TargetAltitude = aircraft.Altitude < cmd.Altitude ? cmd.Altitude : null;
                return CommandDispatcher.Ok($"Maintain VFR at or above {cmd.Altitude:N0}");
            case AltitudeAssignmentModifier.AtOrBelow:
                aircraft.Targets.AltitudeCeiling = cmd.Altitude;
                aircraft.Targets.AltitudeFloor = null;
                aircraft.Targets.TargetAltitude = aircraft.Altitude > cmd.Altitude ? cmd.Altitude : null;
                return CommandDispatcher.Ok($"Maintain VFR at or below {cmd.Altitude:N0}");
            default:
                return new CommandResult(false, $"invalid CM altitude restriction '{cmd.Modifier}'");
        }
    }

    internal static CommandResult ApplyDescendMaintain(DescendMaintainCommand cmd, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        string prev = PreviousAltitude(aircraft, cmd.Altitude);
        CancelViaClearances(aircraft);
        aircraft.Procedure.IsExpediting = false;
        aircraft.Targets.TargetAltitude = cmd.Altitude;
        aircraft.Targets.AltitudeFloor = null;
        aircraft.Targets.AltitudeCeiling = null;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;
        SetPatternAltitudeOverride(aircraft, cmd.Altitude, groundLayout);

        return CommandDispatcher.Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}{prev}");
    }

    internal static CommandResult ApplyForceAltitude(ForceAltitudeCommand cmd, AircraftState aircraft)
    {
        CancelViaClearances(aircraft);
        aircraft.Altitude = cmd.Altitude;
        aircraft.VerticalSpeed = 0;
        aircraft.Targets.TargetAltitude = cmd.Altitude;
        aircraft.Targets.AltitudeFloor = null;
        aircraft.Targets.AltitudeCeiling = null;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;
        return CommandDispatcher.Ok($"Force altitude {cmd.Altitude:N0}");
    }

    internal static CommandResult ApplySpeed(SpeedCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        // A taxiing aircraft: SPD sets the taxi-speed cap, not an airborne target. Reaches here
        // from both direct dispatch and a fired conditional block (AT <taxiway> SPD <n>).
        if (TugReleasePhase.IsTaxiingOrReleasingIntoTaxi(aircraft.Phases?.CurrentPhase))
        {
            return ApplyTaxiSpeed(cmd, aircraft);
        }

        // Reject speed commands to an aircraft inbound to land inside 5nm ON FINAL per §5-7-1.b.4.
        // Only arrivals on an approach / cleared to land are gated — a departure (which
        // also carries an AssignedRunway) and a go-around climbing out are not. SPEEDF
        // (cmd.Force) is the controller override for the cases the rule allows
        // (military, compression).
        if (!cmd.Force && (RefuseInsideFinalGate(aircraft) is { } insideFinal))
        {
            return insideFinal;
        }

        // ATC can't assign a helicopter below the radar-speed minimum; floor a plain SPD to it.
        // Only airborne — ground taxi speed is governed separately. SPEEDF bypasses this floor but is still refused past
        // the type's limits beneath, by RefuseUnflyableSpeed; SPDN (ApplyForceSpeed) bypasses both and may command any speed.
        AircraftCategory spdCat = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool flooredForHeli =
            !cmd.Force && spdCat == AircraftCategory.Helicopter && !aircraft.IsOnGround && cmd.Speed > 0 && cmd.Speed < HelicopterMinRadarSpeedKts;
        int effectiveSpeed = flooredForHeli ? HelicopterMinRadarSpeedKts : cmd.Speed;

        // A speed the type can fly neither here nor at its assigned altitude is refused before anything changes, so the
        // aircraft keeps its current speed. One it can fly at the assigned altitude is accepted, and the cap holds it there.
        // An altitude assigned later in the same block stands in for the assigned one: a comma block carries no "then", so
        // the order is the pilot's (AIM 4-4-12.e; 7110.65 §5-7-2.c).
        if (RefuseUnflyableSpeed(aircraft, ctx, effectiveSpeed, cmd.Modifier, AltitudeToJudgeSpeedAt(aircraft, ctx)) is { } unflyable)
        {
            return unflyable;
        }

        // Any SPD command clears DSR flag and Mach hold
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        aircraft.Targets.TargetMach = null;

        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.SpeedCommandIsControllerIssued = !ctx.IsScenarioScripted;

        // A forced assignment (SPEEDF) persists past the §5-7-1.b.4 auto-cancel gate;
        // a plain SPD clears the override so the gate resumes governing.
        aircraft.Targets.SpeedOverridesFinalGate = cmd.Force;
        aircraft.Targets.SpeedLimitsLifted = false;
        aircraft.Targets.SpeedMaxHeld = false;

        // ATC speed supersedes the procedural last-published-speed memory.
        aircraft.Procedure.LastProcedureSpeedKts = null;

        aircraft.Targets.AssignedSpeed = effectiveSpeed;

        switch (cmd.Modifier)
        {
            case SpeedModifier.Floor:
                aircraft.Targets.SpeedFloor = effectiveSpeed;
                aircraft.Targets.SpeedCeiling = null;
                aircraft.Targets.TargetSpeed = null;
                break;
            case SpeedModifier.Ceiling:
                aircraft.Targets.SpeedCeiling = effectiveSpeed;
                aircraft.Targets.SpeedFloor = null;
                aircraft.Targets.TargetSpeed = null;
                break;
            default:
                aircraft.Targets.TargetSpeed = effectiveSpeed;
                aircraft.Targets.SpeedFloor = null;
                aircraft.Targets.SpeedCeiling = null;
                break;
        }

        NavigationCommandHandler.CancelEarlierCrossingSpeeds(aircraft, ctx.TransmissionId, null);

        if (flooredForHeli)
        {
            aircraft.PendingWarnings.Add(
                $"Speed {cmd.Speed} below helicopter minimum; floored to {HelicopterMinRadarSpeedKts} KIAS [7110.65 §5-7-3.e]"
            );
        }

        return cmd.Modifier switch
        {
            SpeedModifier.Floor => CommandDispatcher.Ok($"Maintain {effectiveSpeed} knots or greater"),
            SpeedModifier.Ceiling => CommandDispatcher.Ok($"Do not exceed {effectiveSpeed} knots"),
            _ => CommandDispatcher.Ok($"Speed {effectiveSpeed}"),
        };
    }

    /// <summary>The §5-7-1.b.4 final gate: the 5 nm from the landing threshold where ATC speed control ends.</summary>
    internal const double FinalGateNm = 5.0;

    /// <summary>
    /// The §5-7-1.b.4 final gate (7110.65): an airborne arrival inbound to land, on final, within <see cref="FinalGateNm"/>
    /// of the landing threshold of the runway it is assigned — its runway when the gate applies, null when it does not.
    /// </summary>
    internal static RunwayInfo? FinalGateRunway(AircraftState aircraft)
    {
        if (aircraft.IsOnGround || !ApproachCommandHandler.IsInboundToLand(aircraft) || (aircraft.Phases?.AssignedRunway is not { } runway))
        {
            return null;
        }

        // Measured to the landing threshold, the same datum the physics auto-cancel uses, so the command and the
        // gate that releases it never disagree about where the five miles start.
        double distanceNm = GeoMath.DistanceNm(aircraft.Position, LandingThreshold.Resolve(runway, aircraft.Ground.Layout));
        return ((distanceNm <= FinalGateNm) && ApproachCommandHandler.IsOnFinal(aircraft, runway, aircraft.Ground.Layout)) ? runway : null;
    }

    /// <summary>
    /// Whether a speed revert may not be applied where the aircraft is: in a hold, whose phase flies the holding speed (a
    /// plain SPD is still accepted there, though a CFIX ends the hold), or inside the 5 nm final gate (7110.65
    /// §5-7-1.b.4), unless a forced speed (<see cref="ControlTargets.SpeedOverridesFinalGate"/>) holds the aircraft past
    /// the gate.
    /// </summary>
    internal static bool SpeedRevertForbidden(AircraftState aircraft) =>
        IsHoldingPhase(aircraft.Phases?.CurrentPhase) || ((FinalGateRunway(aircraft) is not null) && !aircraft.Targets.SpeedOverridesFinalGate);

    /// <summary>
    /// Whether <paramref name="phase"/> keeps the aircraft in a holding orbit, flying the hold's own speed. A level-off
    /// beneath an airspace shelf is not one: the pilot keeps the aircraft at its assigned speed there.
    /// </summary>
    private static bool IsHoldingPhase(Phase? phase) =>
        phase is HoldingPatternPhase or VfrHoldPhase or AirspaceBoundaryHoldPhase { Mode: AirspaceHoldMode.Orbit };

    /// <summary>
    /// The refusal of a speed assignment to an airborne arrival on final inside 5 nm of its landing threshold
    /// (7110.65 §5-7-1.b.4), or null when the gate does not apply.
    /// </summary>
    internal static CommandResult? RefuseInsideFinalGate(AircraftState aircraft) =>
        (FinalGateRunway(aircraft) is not null) ? new CommandResult(false, "Cannot assign speed inside 5nm final [7110.65 §5-7-1.b.4]") : null;

    /// <summary>
    /// The unable for a controller speed (IAS, knots) the type can fly neither at its present altitude nor at
    /// <paramref name="otherAltitudeFt"/> (the altitude it is cleared to, or a crossing's), or null when it can fly it at
    /// either. The maximum is <see cref="AircraftPerformance.MaxOperatingIasKts"/> (the lower of Vmo and Mmo's IAS
    /// equivalent), and the figure named is the one at the present altitude. The minimum is the type's final approach
    /// speed (<see cref="AircraftPerformance.MinOperatingIasKts"/>), except for a helicopter, which has none.
    ///
    /// A type with no known maximum refuses nothing, neither end. A floor (<see cref="SpeedModifier.Floor"/>) only
    /// reaches up and a ceiling only reaches down, so each is checked against its own end alone. A scenario preset is
    /// never refused: the physics cap holds it at the limit instead. Only an airborne aircraft is checked, the same
    /// scope as the physics cap.
    /// </summary>
    internal static CommandResult? RefuseUnflyableSpeed(
        AircraftState aircraft,
        DispatchContext ctx,
        double iasKts,
        SpeedModifier modifier,
        double? otherAltitudeFt
    )
    {
        if (ctx.IsScenarioScripted || aircraft.IsOnGround)
        {
            return null;
        }

        if (AircraftPerformance.MaxOperatingIasKts(aircraft.AircraftType, aircraft.Altitude) is not { } maxHere)
        {
            return null;
        }

        double maxAnywhere = AircraftPerformance.MaxAcceptedIasKts(aircraft.AircraftType, aircraft.Altitude, otherAltitudeFt) ?? maxHere;
        if ((modifier != SpeedModifier.Ceiling) && (iasKts > maxAnywhere))
        {
            return RefuseAsFastest(aircraft, maxHere);
        }

        return modifier != SpeedModifier.Floor ? RefuseBelowMinimum(aircraft, iasKts, maxAnywhere) : null;
    }

    /// <summary>
    /// The unable for a speed below the slowest the type accepts (<see cref="AircraftPerformance.MinAcceptedIasKts"/>), or
    /// null: a helicopter, and a type whose minimum sits above its maximum (<paramref name="maxIasKts"/>) at every altitude
    /// checked, have none.
    /// </summary>
    private static CommandResult? RefuseBelowMinimum(AircraftState aircraft, double iasKts, double maxIasKts)
    {
        double min = AircraftPerformance.MinAcceptedIasKts(aircraft.AircraftType, maxIasKts);
        return iasKts >= min ? null : SpeedLimitRefusal(aircraft, PilotResponder.SpeedLimitKind.SlowestKnots, SlowestNamedKts(min));
    }

    /// <summary>
    /// The altitude other than the present one a controller speed is judged at: the altitude assigned later in the speed's
    /// own block (<see cref="DispatchContext.AltitudeAssignedLaterInBlock"/>, <c>SPD 300, DM 240</c>), else the altitude the
    /// aircraft is assigned. A comma block carries no "then", so the order is the pilot's (AIM 4-4-12.e; 7110.65 §5-7-2.c).
    /// </summary>
    private static double? AltitudeToJudgeSpeedAt(AircraftState aircraft, DispatchContext ctx) =>
        ctx.AltitudeAssignedLaterInBlock ?? aircraft.Targets.AssignedAltitude;

    /// <summary>
    /// The knots step a named limit is rounded to: ATC assigns speeds in 5 kt steps (7110.65 §5-7-1.a.7; AIM 4-4-12.b), and
    /// the pilot names a speed it can fly when unable (AIM 4-4-12.h).
    /// </summary>
    private const double NamedSpeedStepKts = 5;

    /// <summary>
    /// The fastest speed named in an unable: <paramref name="maxIasKts"/> floored to a multiple of 5 kt, so it is one the
    /// type can fly. The epsilon keeps a figure already on the step (335) from flooring to the one below through float error.
    /// </summary>
    internal static double FastestNamedKts(double maxIasKts) => Math.Floor((maxIasKts / NamedSpeedStepKts) + 1e-9) * NamedSpeedStepKts;

    /// <summary>
    /// The slowest speed named in an unable: <paramref name="minIasKts"/> raised to a multiple of 5 kt, so it is one the
    /// type can fly. The epsilon keeps a figure already on the step (140) from rising to the one above through float error.
    /// </summary>
    internal static double SlowestNamedKts(double minIasKts) => Math.Ceiling((minIasKts / NamedSpeedStepKts) - 1e-9) * NamedSpeedStepKts;

    /// <summary>
    /// The unable naming the fastest speed the type can fly at its present altitude, <paramref name="maxIasKts"/>. Named
    /// as a Mach number when Mmo is the term that governs there (the aircraft is above its IAS/Mach crossover); otherwise
    /// in knots, floored to a multiple of 5 kt (<see cref="FastestNamedKts"/>), so the named figure is always one it can fly.
    /// </summary>
    private static CommandResult RefuseAsFastest(AircraftState aircraft, double maxIasKts)
    {
        if (
            (AircraftPerformance.MaxOperatingMach(aircraft.AircraftType) is { } mmo)
            && (WindInterpolator.MachToIas(mmo, aircraft.Altitude) <= maxIasKts)
        )
        {
            return RefuseAsFastestMach(aircraft, mmo);
        }

        return SpeedLimitRefusal(aircraft, PilotResponder.SpeedLimitKind.FastestKnots, FastestNamedKts(maxIasKts));
    }

    /// <summary>The unable naming Mmo, <paramref name="mmo"/>, floored to the hundredth, as the fastest Mach the type can fly.</summary>
    private static CommandResult RefuseAsFastestMach(AircraftState aircraft, double mmo) =>
        // The epsilon keeps a figure already on the hundredth (0.82) from flooring to the one below through float error.
        SpeedLimitRefusal(aircraft, PilotResponder.SpeedLimitKind.FastestMach, Math.Floor((mmo * 100) + 1e-9) / 100);

    private static CommandResult SpeedLimitRefusal(AircraftState aircraft, PilotResponder.SpeedLimitKind kind, double figure)
    {
        PilotSpeechText unable = PilotResponder.BuildUnableSpeedLimit(aircraft, kind, figure);
        return new CommandResult(false, char.ToUpperInvariant(unable.Terminal[0]) + unable.Terminal[1..]) { PilotUnable = unable };
    }

    /// <summary>
    /// SPD on a taxiing aircraft: set the taxi-speed cap (<see cref="AircraftGroundOps.CommandedTaxiSpeedKts"/>),
    /// clamped to [<see cref="CategoryPerformance.MinCommandedTaxiSpeedKts"/>,
    /// <see cref="CategoryPerformance.MaxCommandedTaxiSpeed"/>]. Ceiling only — the navigator's
    /// corner/arc/braking/conflict caps still win. Floor/ceiling modifiers (<c>+</c>/<c>-</c>) have
    /// no taxi meaning and are treated as a plain cap.
    /// </summary>
    private static CommandResult ApplyTaxiSpeed(SpeedCommand cmd, AircraftState aircraft)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double defaultSpeed = CategoryPerformance.TaxiSpeed(cat);
        double clamped = Math.Clamp((double)cmd.Speed, CategoryPerformance.MinCommandedTaxiSpeedKts, CategoryPerformance.MaxCommandedTaxiSpeed(cat));

        aircraft.Ground.CommandedTaxiSpeedKts = clamped;
        // The numeric taxi cap supersedes expedite (the fixed-1.3x variant of the same knob).
        GroundCommandHandler.EndRouteExpedite(aircraft);

        string suffix =
            clamped < defaultSpeed ? ", reduce speed"
            : clamped > defaultSpeed ? ", without delay"
            : "";
        return CommandDispatcher.Ok($"Taxi at {clamped:F0} knots{suffix}");
    }

    /// <summary>
    /// Ends the ATC speed assignment in force: the assigned and target speed, floor, ceiling and Mach, and the
    /// procedural last-published-speed memory, so the procedure's published speeds and the default schedule take the
    /// speed back. An approach clearance does it, cancelling any previously assigned speed (7110.65 5-7-1.d; AIM
    /// 4-4-12.g). A climb via or descend via clearance and "resume normal" or "resume published speed" go through
    /// <see cref="CancelAssignedSpeedKeepingProcedureFloor"/>, which keeps a floor the procedure set.
    /// </summary>
    internal static void CancelAssignedSpeed(AircraftState aircraft)
    {
        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.AssignedSpeed = null;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.HasExplicitSpeedCommand = false;
        aircraft.Targets.SpeedCommandIsControllerIssued = false;
        aircraft.Targets.SpeedOverridesFinalGate = false;
        aircraft.Targets.SpeedLimitsLifted = false;
        aircraft.Targets.SpeedMaxHeld = false;
        aircraft.Targets.AssignedSpeedTransmissionId = null;
        aircraft.Procedure.LastProcedureSpeedKts = null;
    }

    /// <summary>
    /// Ends the ATC assignment a speed bound stood for, as a phase clearing the bound drops it: "maintain 280 or greater"
    /// and "do not exceed 280" are a bound, not a speed to fly, so an assignment left behind after the bound goes would
    /// read as an exact 280 and be flown that way where nothing caps it. An exact assignment's bounds never equal it, so
    /// one is kept as it was.
    /// </summary>
    internal static void EndBoundWithTheAssignment(ControlTargets targets, double? clearedBound)
    {
        if ((clearedBound is not null) && (targets.AssignedSpeed == clearedBound))
        {
            targets.AssignedSpeed = null;
            targets.HasExplicitSpeedCommand = false;
        }
    }

    /// <summary>
    /// Touchdown ends the ATC speed assignment in force: the pilot who lands is done flying the speed it was given
    /// (7110.65 §5-7-1.b.4; AIM 4-4-12.g), and a touch-and-go is a landing followed by a departure from the touchdown
    /// on, so its climb-out is flown at the type's own pattern speed rather than at the arrival's assignment (§5-7-3.d).
    /// Everything <see cref="CancelAssignedSpeed"/> clears goes, the maximum-forward-speed hold and its Mach included. A
    /// go-around before touchdown keeps its assignment, so this is called from the touchdown sites alone.
    /// </summary>
    internal static void CancelAssignedSpeedAtTouchdown(AircraftState aircraft) => CancelAssignedSpeed(aircraft);

    /// <summary>
    /// <see cref="CancelAssignedSpeed"/>, keeping a floor the procedure set: a minimum the aircraft crossed at a published
    /// "at or above" speed, which the physics writes only with no explicit ATC speed in force. A via clearance and "resume
    /// normal speed" cancel the speeds ATC assigned, not a published restriction (7110.65 5-7-1.d NOTE, 5-7-1.g NOTE 3;
    /// P/CG RESUME NORMAL SPEED); the floor gives way when the pilot slows for the next lower published speed. Past the
    /// procedure nothing published stands behind it, so it ends with the rest (7110.65 5-7-4.a).
    /// </summary>
    internal static void CancelAssignedSpeedKeepingProcedureFloor(AircraftState aircraft)
    {
        bool onProcedure = (aircraft.Procedure.ActiveSidId is not null) || (aircraft.Procedure.ActiveStarId is not null);
        double? procedureFloor = (onProcedure && !aircraft.Targets.HasExplicitSpeedCommand) ? aircraft.Targets.SpeedFloor : null;
        CancelAssignedSpeed(aircraft);
        aircraft.Targets.SpeedFloor = procedureFloor;
    }

    /// <summary>
    /// A vector or a direct, even to a fix still on the procedure, ends the procedure's speeds (7110.65 5-7-1.e NOTE, "issues
    /// a vector, assigns a direct route"): the floor crossed at a published "at or above" speed and the last-published-speed
    /// memory go with it. With an explicit ATC speed in force the floor is ATC's and stands.
    /// </summary>
    internal static void EndPublishedSpeedFloor(AircraftState aircraft)
    {
        if (!aircraft.Targets.HasExplicitSpeedCommand)
        {
            aircraft.Targets.SpeedFloor = null;
            aircraft.Procedure.LastProcedureSpeedKts = null;
        }
    }

    /// <summary>The ATC speed fields <see cref="CancelAssignedSpeed"/> clears, captured so a speed restated with a clearance can stand.</summary>
    internal readonly record struct AssignedSpeedState(
        double? TargetSpeed,
        double? AssignedSpeed,
        double? SpeedFloor,
        double? SpeedCeiling,
        double? TargetMach,
        bool HasExplicitSpeedCommand,
        bool SpeedCommandIsControllerIssued,
        bool SpeedOverridesFinalGate,
        bool SpeedLimitsLifted,
        bool SpeedMaxHeld,
        int? AssignedSpeedTransmissionId,
        double? LastProcedureSpeedKts
    )
    {
        internal static AssignedSpeedState Capture(AircraftState aircraft) =>
            new(
                aircraft.Targets.TargetSpeed,
                aircraft.Targets.AssignedSpeed,
                aircraft.Targets.SpeedFloor,
                aircraft.Targets.SpeedCeiling,
                aircraft.Targets.TargetMach,
                aircraft.Targets.HasExplicitSpeedCommand,
                aircraft.Targets.SpeedCommandIsControllerIssued,
                aircraft.Targets.SpeedOverridesFinalGate,
                aircraft.Targets.SpeedLimitsLifted,
                aircraft.Targets.SpeedMaxHeld,
                aircraft.Targets.AssignedSpeedTransmissionId,
                aircraft.Procedure.LastProcedureSpeedKts
            );

        internal void Restore(AircraftState aircraft)
        {
            aircraft.Targets.TargetSpeed = TargetSpeed;
            aircraft.Targets.AssignedSpeed = AssignedSpeed;
            aircraft.Targets.SpeedFloor = SpeedFloor;
            aircraft.Targets.SpeedCeiling = SpeedCeiling;
            aircraft.Targets.TargetMach = TargetMach;
            aircraft.Targets.HasExplicitSpeedCommand = HasExplicitSpeedCommand;
            aircraft.Targets.SpeedCommandIsControllerIssued = SpeedCommandIsControllerIssued;
            aircraft.Targets.SpeedOverridesFinalGate = SpeedOverridesFinalGate;
            aircraft.Targets.SpeedLimitsLifted = SpeedLimitsLifted;
            aircraft.Targets.SpeedMaxHeld = SpeedMaxHeld;
            aircraft.Targets.AssignedSpeedTransmissionId = AssignedSpeedTransmissionId;
            aircraft.Procedure.LastProcedureSpeedKts = LastProcedureSpeedKts;
        }
    }

    /// <summary>
    /// On a STAR, past a fix with a published "at" or "at or below" speed the pilot keeps that speed until the next
    /// published speed restriction (AIM 5-4-1.a, NOTE 2; 7110.65 5-7-1.g NOTE 3), so once an ATC speed is cancelled the
    /// last crossed published speed (<see cref="AircraftProcedure.LastCrossedPublishedSpeedKts"/>) becomes the ceiling
    /// again. 14 CFR 91.117 still caps it in the physics. Nothing off a STAR or before the first such crossing.
    /// </summary>
    internal static void RestoreCrossedPublishedCeiling(AircraftState aircraft)
    {
        if ((aircraft.Procedure.ActiveStarId is not null) && (aircraft.Procedure.LastCrossedPublishedSpeedKts is { } crossedKts))
        {
            aircraft.Targets.SpeedCeiling = crossedKts;
        }
    }

    /// <summary>
    /// On a STAR, a resumed speed goes back to the published one: "resume normal speed" does not cancel a procedure's
    /// published restrictions (AIM 4-4-12.f), so the last crossed published speed is the ceiling again
    /// (<see cref="RestoreCrossedPublishedCeiling"/>). A next published speed above the present one, or an "at or above"
    /// minimum, is targeted at once; a lower one is left to the planner's deceleration timing
    /// (<see cref="FlightPhysics.ApplyNextPublishedSpeed"/>). Nothing is held while the published speeds are deleted (DSR).
    /// </summary>
    private static void ResumePublishedStarSpeeds(AircraftState aircraft)
    {
        if ((aircraft.Procedure.ActiveStarId is null) || aircraft.Procedure.SpeedRestrictionsDeleted)
        {
            return;
        }

        RestoreCrossedPublishedCeiling(aircraft);
        FlightPhysics.ApplyNextPublishedSpeed(aircraft);
    }

    internal static CommandResult ApplyResumeNormalSpeed(AircraftState aircraft, DispatchContext ctx) =>
        ResumeSpeed(aircraft, ctx, SpeedResumeKind.Normal, "Resume normal speed");

    /// <summary>
    /// RPS, "resume published speed": everything "resume normal speed" does, and it also undoes a DSR, so the
    /// procedure's published speeds apply again and the last crossed one is held as the ceiling.
    /// </summary>
    internal static CommandResult ApplyResumePublishedSpeed(AircraftState aircraft, DispatchContext ctx) =>
        ResumeSpeed(aircraft, ctx, SpeedResumeKind.Published, "Resume published speed");

    /// <summary>
    /// The end state of RNS, RPS or DSR (<paramref name="kind"/>), written by the command and, for one given in a
    /// crossing's own transmission, past the crossing's fix (<see cref="NavigationTarget.RevertResume"/>). RNS ends the
    /// ATC assignment, keeping a floor the procedure set, and on a STAR resumes its published speeds; RPS also undoes a
    /// DSR first; DSR ends the assignment and deletes the published speeds.
    /// </summary>
    internal static void ApplySpeedResume(AircraftState aircraft, SpeedResumeKind kind)
    {
        if (kind == SpeedResumeKind.Deleted)
        {
            CancelAssignedSpeed(aircraft);
            aircraft.Procedure.SpeedRestrictionsDeleted = true;
            return;
        }

        if (kind == SpeedResumeKind.Published)
        {
            aircraft.Procedure.SpeedRestrictionsDeleted = false;
        }

        CancelAssignedSpeedKeepingProcedureFloor(aircraft);
        ResumePublishedStarSpeeds(aircraft);
    }

    private static CommandResult ResumeSpeed(AircraftState aircraft, DispatchContext ctx, SpeedResumeKind kind, string readback)
    {
        // On a taxiing aircraft, resume the category-default taxi speed (clear the cap/expedite);
        // the airborne speed targets below are navigator-owned on the ground and left untouched.
        if (TugReleasePhase.IsTaxiingOrReleasingIntoTaxi(aircraft.Phases?.CurrentPhase))
        {
            aircraft.Ground.CommandedTaxiSpeedKts = null;
            GroundCommandHandler.EndRouteExpedite(aircraft);
            return CommandDispatcher.Ok("Resume normal taxi speed");
        }

        NavigationCommandHandler.CancelEarlierCrossingSpeeds(aircraft, ctx.TransmissionId, kind);

        // Clearing the fields above is the whole command in most cases, but on a final approach it can leave the
        // arrival flying exactly what it already was — and an answer of "Resume normal speed" then reads as a speed
        // change the instructor never gets. Say which of the two happened.
        bool alreadyOnProfile = RestoreFinalApproachProfileSpeed(aircraft);
        return CommandDispatcher.Ok(alreadyOnProfile ? $"{readback} — already on its final approach speed profile, no change" : readback);
    }

    /// <summary>
    /// Gives an aircraft on final the profile speed back, because clearing the fields above does not. AIM 4-4-12.f.1:
    /// "resume normal speed" terminates the ATC speed adjustment and the pilot returns to the normal profile — but
    /// <see cref="FinalApproachPhase.ManagesSpeed"/> suppresses the physics auto-schedule, and the phase writes no
    /// speed target of its own before its deceleration stages, which only ever bleed downward. So an arrival slowed on
    /// a long final and then told to resume simply kept the slow speed, the same gap the generator stream closes with
    /// <c>SimulationEngine.RestoreManagedSpeed</c>, and this is that judgement applied to the command: the speed is
    /// <see cref="ArrivalSpacingManager.ScheduledFinalSpeedKts"/> at the aircraft's own distance, issued only when it
    /// is more than <see cref="ArrivalSpacingManager.SpeedRestoreDeadbandKts"/> slow (inside that band the pilot is
    /// complying, AIM 4-4-12.c) and still outside <see cref="ArrivalSpacingManager.SpeedRestoreGateNm"/>, so nothing is
    /// sped up moments before the phase would slow it again.
    ///
    /// <para>The target written here is not an explicit speed command:
    /// <see cref="ControlTargets.HasExplicitSpeedCommand"/> and
    /// <see cref="ControlTargets.SpeedCommandIsControllerIssued"/> stay false, so the phase's own stages take the
    /// aircraft over at their gates exactly as they would have, and <see cref="FlightPhysics"/> nulls the target as
    /// soon as it is reached.</para>
    /// </summary>
    /// <returns>
    /// True when the arrival is on a final-approach profile and was already flying it, so nothing was handed back —
    /// inside <see cref="ArrivalSpacingManager.SpeedRestoreGateNm"/>, or within
    /// <see cref="ArrivalSpacingManager.SpeedRestoreDeadbandKts"/> of the scheduled speed. False both when the profile
    /// speed was restored and when there is no final-approach profile to be on, which the caller answers alike: the
    /// one thing the command's answer distinguishes is a speed that did not move.
    /// </returns>
    private static bool RestoreFinalApproachProfileSpeed(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is not FinalApproachPhase finalApproach)
        {
            return false;
        }

        // The phase caches the distance each tick; before its first one it still holds the sentinel, and a speed
        // scheduled at "MaxValue nm" would be the clean speed for an aircraft whose position is not yet known here.
        // With no distance there is no schedule either, so this is not "already on profile" — it is unknown.
        double distanceNm = finalApproach.DistanceToThresholdNm;
        if (!double.IsFinite(distanceNm) || (distanceNm >= double.MaxValue))
        {
            return false;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double vref = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category);
        double scheduled = ArrivalSpacingManager.ScheduledFinalSpeedKts(aircraft.AircraftType, category, vref, aircraft.Callsign, distanceNm);
        if (
            (aircraft.IndicatedAirspeed < scheduled - ArrivalSpacingManager.SpeedRestoreDeadbandKts)
            && (distanceNm >= ArrivalSpacingManager.SpeedRestoreGateNm(category, aircraft.Callsign))
        )
        {
            aircraft.Targets.TargetSpeed = scheduled;
            return false;
        }

        return true;
    }

    internal static CommandResult ApplyReduceToFinalApproachSpeed(AircraftState aircraft, DispatchContext ctx)
    {
        double approachSpeed = FinalApproachSpeed(aircraft);
        aircraft.Targets.TargetSpeed = approachSpeed;
        aircraft.Targets.AssignedSpeed = approachSpeed;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.SpeedCommandIsControllerIssued = !ctx.IsScenarioScripted;
        aircraft.Targets.SpeedOverridesFinalGate = false;
        aircraft.Targets.SpeedLimitsLifted = false;
        aircraft.Targets.SpeedMaxHeld = false;
        NavigationCommandHandler.CancelEarlierCrossingSpeeds(aircraft, ctx.TransmissionId, null);
        aircraft.Procedure.LastProcedureSpeedKts = null;
        return CommandDispatcher.Ok($"Reduce to final approach speed ({approachSpeed:F0} kts)");
    }

    /// <summary>The type's final approach speed, the speed RFAS reduces to and SPDN RFAS sets.</summary>
    private static double FinalApproachSpeed(AircraftState aircraft) =>
        AircraftPerformance.ApproachSpeed(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));

    internal static CommandResult ApplyDeleteSpeedRestrictions(AircraftState aircraft, DispatchContext ctx)
    {
        NavigationCommandHandler.CancelEarlierCrossingSpeeds(aircraft, ctx.TransmissionId, SpeedResumeKind.Deleted);
        return CommandDispatcher.Ok("Speed restrictions deleted");
    }

    internal static CommandResult ApplyExpedite(ExpediteCommand cmd, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        // Runway-exit expedite: a just-landed aircraft (flaring/rolling-out/exiting,
        // no taxi route yet) clears the runway as fast as possible. Keyed on the
        // active landing/exit phase so it never hijacks expedite-descent on short
        // final or expedite-taxi after pushback.
        if (cmd.Altitude is null && GroundCommandHandler.HasLandingOrExitPhase(aircraft, PhaseStatus.Active))
        {
            aircraft.Ground.IsExpeditingExit = true;

            // Drop any cached exit candidate so LandingPhase re-resolves at the
            // higher braking limit and can take an earlier exit. (The
            // ER/EL/EXIT EXP form re-resolves via the preference-change path.)
            foreach (Phase phase in aircraft.Phases!.Phases)
            {
                if (phase is LandingPhase landing)
                {
                    landing.ResetExitCandidate();
                }
            }

            return CommandDispatcher.Ok("Exit without delay");
        }

        // Ground-context expedite: the route expedite (AircraftGroundOps.IsExpeditingTaxi), until whatever ends it.
        // Only meaningful with an active taxi route; pushback/parking are out of scope.
        if (aircraft.IsOnGround && cmd.Altitude is null)
        {
            if (aircraft.Ground.AssignedTaxiRoute is null)
            {
                return new CommandResult(false, "Expedite taxi requires an assigned taxi route");
            }

            aircraft.Ground.IsExpeditingTaxi = true;
            // Expedite supersedes any numeric taxi-speed cap (mutually exclusive knobs).
            aircraft.Ground.CommandedTaxiSpeedKts = null;
            return CommandDispatcher.Ok("Expedite taxi");
        }

        // EXP <alt> carries its own clearance — assign the altitude, then expedite to it.
        // To expedite through an intermediate altitude and resume the normal rate there,
        // chain the generic level condition instead: `EXP; LV 050 NORM`.
        if (cmd.Altitude is { } assigned)
        {
            return ApplyExpediteToAltitude(assigned, aircraft, groundLayout);
        }

        // Bare EXP only makes sense while the aircraft is actually going somewhere
        // vertically. Ask physics the same question it asks itself each tick, so the
        // two can never disagree about whether a climb or descent is running.
        // Rejection text is spoken by the pilot in solo mode ("unable, {reason}"), so it
        // reads as a standalone clause with no interior dash and no grouped numerals.
        if (FlightPhysics.ResolveAltitudeGoal(aircraft) is not { } goal)
        {
            return new CommandResult(
                false,
                aircraft.Targets.AssignedAltitude is { } level
                    ? $"Unable, level at {(int)level}, no climb or descent to expedite"
                    : "Unable, no climb or descent in progress"
            );
        }

        aircraft.Procedure.IsExpediting = true;
        return CommandDispatcher.Ok(goal > aircraft.Altitude ? "Expedite climb" : "Expedite descent");
    }

    /// <summary>
    /// Assigns <paramref name="assigned"/> exactly as CM/DM would — reusing those handlers
    /// keeps the via-mode, floor/ceiling and pattern-override bookkeeping in one place — and
    /// then turns expedite on. Order matters: CM/DM both clear <c>IsExpediting</c>.
    /// </summary>
    private static CommandResult ApplyExpediteToAltitude(int assigned, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (Math.Abs(assigned - aircraft.Altitude) < FlightPhysics.AltitudeSnapFt)
        {
            return new CommandResult(false, $"Unable, already level at {assigned}");
        }

        bool climb = assigned > aircraft.Altitude;
        CommandResult assignment = climb
            ? ApplyClimbMaintain(new ClimbMaintainCommand(assigned), aircraft, groundLayout)
            : ApplyDescendMaintain(new DescendMaintainCommand(assigned), aircraft, groundLayout);

        if (!assignment.Success)
        {
            return assignment;
        }

        aircraft.Procedure.IsExpediting = true;
        return CommandDispatcher.Ok($"{AltitudeVerb(aircraft, assigned)} {assigned}, expedite {(climb ? "climb" : "descent")}");
    }

    internal static CommandResult ApplyNormalRate(AircraftState aircraft)
    {
        aircraft.Procedure.IsExpediting = false;
        aircraft.Ground.IsExpeditingExit = false;
        aircraft.Ground.IsExpeditingLineup = false;
        GroundCommandHandler.EndRouteExpedite(aircraft);
        aircraft.Targets.DesiredVerticalRate = null;
        return CommandDispatcher.Ok("Resume normal rate");
    }

    internal static CommandResult ApplyMach(MachCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        // Past Mmo is past the maximum whatever the altitude, and is answered in Mach below the crossover too. A convention:
        // AIM 4-4-12.b sets ATC's frame, and the pilot answers in it. Otherwise the Mach number's IAS equivalent here is checked against both ends, since a
        // Mach hold low down can run past Vmo or sit below the final approach speed.
        if (
            !ctx.IsScenarioScripted
            && !aircraft.IsOnGround
            && (AircraftPerformance.MaxOperatingMach(aircraft.AircraftType) is { } mmo)
            && (cmd.MachNumber > mmo)
        )
        {
            return RefuseAsFastestMach(aircraft, mmo);
        }

        // As with SPD, a Mach number the type can fly at its assigned altitude (or the one its own block assigns) is accepted
        // even when it cannot fly it here.
        double machIas = WindInterpolator.MachToIas(cmd.MachNumber, aircraft.Altitude);
        if (
            (RefuseUnflyableSpeed(aircraft, ctx, machIas, SpeedModifier.None, otherAltitudeFt: null) is { } unflyable)
            && !IsMachFlyableAt(aircraft, cmd.MachNumber, AltitudeToJudgeSpeedAt(aircraft, ctx))
        )
        {
            return unflyable;
        }

        aircraft.Targets.TargetMach = cmd.MachNumber;
        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.AssignedSpeed = null;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.SpeedCommandIsControllerIssued = !ctx.IsScenarioScripted;
        aircraft.Targets.SpeedOverridesFinalGate = false;
        aircraft.Targets.SpeedLimitsLifted = false;
        aircraft.Targets.SpeedMaxHeld = false;
        NavigationCommandHandler.CancelEarlierCrossingSpeeds(aircraft, ctx.TransmissionId, null);
        aircraft.Procedure.LastProcedureSpeedKts = null;
        return CommandDispatcher.Ok($"Maintain Mach {cmd.MachNumber:F2}");
    }

    /// <summary>
    /// SMAX, "maintain maximum forward speed" (7110.65 5-7-2): an ATC speed assignment, like SPD, to the fastest the type
    /// may fly where it is. <see cref="FlightPhysics.HoldMaximumForwardSpeed"/> sets the speed now and keeps it each tick,
    /// a margin under the redline: Vmo less 10 kt below the IAS/Mach crossover, Mmo less 0.02 above it. Refused on the ground, inside the 5 nm final gate as a plain SPD
    /// is, and for a type with no known maximum.
    /// </summary>
    internal static CommandResult ApplySpeedMax(AircraftState aircraft, DispatchContext ctx)
    {
        if (aircraft.IsOnGround)
        {
            return new CommandResult(false, "Maximum forward speed requires the aircraft to be airborne");
        }

        if (RefuseInsideFinalGate(aircraft) is { } insideFinal)
        {
            return insideFinal;
        }

        if (
            (AircraftPerformance.MaxOperatingSpeedKts(aircraft.AircraftType) is null)
            && (AircraftPerformance.MaxOperatingMach(aircraft.AircraftType) is null)
        )
        {
            return new CommandResult(false, $"Unable, no maximum speed is known for {aircraft.AircraftType}");
        }

        ArmSpeedMax(aircraft, controllerIssued: !ctx.IsScenarioScripted);
        NavigationCommandHandler.CancelEarlierCrossingSpeeds(aircraft, ctx.TransmissionId, null);
        return CommandDispatcher.Ok("Maintain maximum forward speed");
    }

    /// <summary>
    /// Puts SMAX in force, replacing whatever ATC speed was assigned: the command does it, and so does passing a crossing
    /// fix whose speed was restated while SMAX was in force (<see cref="NavigationTarget.RevertSpeedMax"/>).
    /// </summary>
    internal static void ArmSpeedMax(AircraftState aircraft, bool controllerIssued)
    {
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.AssignedSpeed = null;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.SpeedCommandIsControllerIssued = controllerIssued;
        aircraft.Targets.SpeedOverridesFinalGate = false;
        aircraft.Targets.SpeedLimitsLifted = false;
        aircraft.Procedure.LastProcedureSpeedKts = null;
        aircraft.Targets.SpeedMaxHeld = true;
        FlightPhysics.HoldMaximumForwardSpeed(aircraft);
    }

    /// <summary>
    /// Ends any Mach hold and SMAX for an act that replaces the speed now (a crossing speed kept by a later speed
    /// instruction of its transmission, a hold, WARP): both a Mach hold SMAX set and one
    /// a MACH command set go, so the new speed is flown rather than rewritten each tick from the old Mach. A crossing
    /// speed on its own ends SMAX only (<see cref="EndSpeedMaxHold"/>).
    /// </summary>
    internal static void EndMachHoldAndSpeedMax(AircraftState aircraft)
    {
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.SpeedMaxHeld = false;
    }

    /// <summary>
    /// Ends SMAX for a crossing speed (a CFIX, a constrained direct) and leaves any Mach hold live, SMAX's own above the
    /// crossover included: the speed planner ends it where the crossing speed takes over
    /// (<see cref="FlightPhysics"/>'s <c>PlanCrossingUnderMach</c>), so the IAS follows the Mach until then rather than
    /// freezing at its figure when the crossing was issued.
    /// </summary>
    internal static void EndSpeedMaxHold(AircraftState aircraft) => aircraft.Targets.SpeedMaxHeld = false;

    /// <summary>
    /// True when the type can fly <paramref name="mach"/> at <paramref name="altitudeFt"/>
    /// (<see cref="AircraftPerformance.IsMachFlyableAt"/>, the client's speed picker's judge too). False with no altitude.
    /// </summary>
    private static bool IsMachFlyableAt(AircraftState aircraft, double mach, double? altitudeFt) =>
        (altitudeFt is { } altitude) && AircraftPerformance.IsMachFlyableAt(aircraft.AircraftType, mach, altitude);

    internal static CommandResult ApplyForceSpeed(ForceSpeedCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        ForceSpeed(aircraft, cmd.Speed, ctx);
        return CommandDispatcher.Ok($"Force speed {cmd.Speed}");
    }

    internal static CommandResult ApplyForceFinalApproachSpeed(AircraftState aircraft, DispatchContext ctx)
    {
        double approachSpeed = FinalApproachSpeed(aircraft);
        ForceSpeed(aircraft, approachSpeed, ctx);
        return CommandDispatcher.Ok($"Force final approach speed ({approachSpeed:F0} kts)");
    }

    private static void ForceSpeed(AircraftState aircraft, double speed, DispatchContext ctx)
    {
        // Force-speed is an immediate override and intentionally exempt from the helicopter
        // radar-speed floor in ApplySpeed — it may command any speed.
        aircraft.IndicatedAirspeed = speed;
        aircraft.Targets.TargetSpeed = speed;
        aircraft.Targets.AssignedSpeed = speed;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.SpeedCommandIsControllerIssued = !ctx.IsScenarioScripted;
        // A forced speed survives the §5-7-1.b.4 auto-cancel gate, same as SPEEDF. Unlike SPEEDF it is also flown past
        // 14 CFR 91.117 and the type's Vmo/Mmo, scripted or not.
        aircraft.Targets.SpeedOverridesFinalGate = true;
        aircraft.Targets.SpeedLimitsLifted = true;
        aircraft.Targets.SpeedMaxHeld = false;
        NavigationCommandHandler.CancelEarlierCrossingSpeeds(aircraft, ctx.TransmissionId, null);
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        aircraft.Procedure.LastProcedureSpeedKts = null;
    }

    internal static CommandResult ApplySquawk(SquawkCommand cmd, AircraftState aircraft)
    {
        aircraft.Transponder.Code = cmd.Code;
        // Squawking 1200 by instruction IS squawking VFR, so this latches off the assigned-vs-reported
        // mismatch flash exactly as SQV does; any other code is a real assignment and releases it.
        aircraft.Transponder.CommandedSquawkVfr = (cmd.Code == 1200);
        return CommandDispatcher.Ok($"Squawk {cmd.Code:D4}");
    }

    internal static CommandResult ApplySquawkReset(AircraftState aircraft)
    {
        aircraft.Transponder.Code = aircraft.Transponder.AssignedCode;
        // Back on the assigned code: the reported-vs-assigned mismatch the squawk-VFR latch hid is resolved,
        // so the flash resumes.
        aircraft.Transponder.CommandedSquawkVfr = false;
        return CommandDispatcher.Ok($"Squawk {aircraft.Transponder.AssignedCode:D4}");
    }

    internal static CommandResult ApplySquawkVfr(AircraftState aircraft)
    {
        aircraft.Transponder.Code = 1200;
        // Latch off the RPO's assigned-vs-reported mismatch flash: the pilot is now squawking VFR by
        // instruction (as with SQ 1200), so the stale assigned discrete code is noise. Released by a new
        // assigned code, by SQ <other code>, or by a bare SQ that puts the pilot back on the assigned code.
        aircraft.Transponder.CommandedSquawkVfr = true;
        return CommandDispatcher.Ok("Squawk VFR");
    }

    internal static CommandResult ApplySquawkNormal(AircraftState aircraft)
    {
        aircraft.Transponder.Mode = "C";
        return CommandDispatcher.Ok("Squawk normal");
    }

    internal static CommandResult ApplySquawkStandby(AircraftState aircraft)
    {
        aircraft.Transponder.Mode = "Standby";
        return CommandDispatcher.Ok("Squawk standby");
    }

    internal static CommandResult ApplyIdent(AircraftState aircraft)
    {
        aircraft.Transponder.IsIdenting = true;
        return CommandDispatcher.Ok("Ident");
    }

    internal static CommandResult ApplyRandomSquawk(AircraftState aircraft, Random rng)
    {
        aircraft.Transponder.Code = SimulationWorld.GenerateBeaconCode(rng);
        return CommandDispatcher.Ok($"Squawk {aircraft.Transponder.Code:D4}");
    }

    /// <summary>
    /// Refuses a direct-to that would fly an aircraft backwards down a one-way military route, or
    /// null when the clearance is fine.
    ///
    /// AP/1B routes are one-way and course reversals are prohibited (chapter 1 §V.B.1). The guard
    /// cannot live in <see cref="Phases.MilitaryRoutePhase"/>: that phase returns
    /// <c>ClearsPhase</c> for a direct-to, so by the time it could object it has already been torn
    /// down and the aircraft flies to the point with no protection left. Going direct to a point
    /// still <em>ahead</em> stays legal — that is an ordinary shortcut along the route.
    /// </summary>
    internal static CommandResult? RejectBackwardsMilitaryRouteDirect(IReadOnlyList<ResolvedFix> fixes, AircraftState aircraft)
    {
        AircraftMilitaryRoute state = aircraft.MilitaryRoute;
        if (!state.IsActive || state.Designator is null || state.CurrentSegmentIndex < 0)
        {
            return null;
        }

        MilitaryRoute? route = Data.NavigationDatabase.Instance.GetMilitaryRoute(state.Designator);
        if (route is null)
        {
            return null;
        }

        foreach (ResolvedFix fix in fixes)
        {
            int index = IndexOfRoutePoint(route, fix.Name);
            if (index >= 0 && index < state.CurrentSegmentIndex)
            {
                return new CommandResult(false, $"Unable, {route.Printed} is one-way and {fix.Name} is behind the aircraft (AP/1B chapter 1 §V.B.1)");
            }
        }

        return null;
    }

    /// <summary>The index of a synthetic route-point name on the cleared direction, or -1.</summary>
    private static int IndexOfRoutePoint(Data.MilitaryRoutes.MilitaryRoute route, string name)
    {
        for (int i = 0; i < route.Points.Count; i++)
        {
            if (string.Equals(route.Points[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    internal static CommandResult ApplyDirectTo(DirectToCommand cmd, AircraftState aircraft, bool validateDctFixes)
    {
        if (validateDctFixes)
        {
            HashSet<string> programmed = aircraft.GetProgrammedFixes();
            if (programmed.Count > 0)
            {
                var unprogrammed = cmd.Fixes.Where(f => !programmed.Contains(f.Name)).ToList();
                if (unprogrammed.Count > 0)
                {
                    string names = FixListCommas(unprogrammed);
                    return new CommandResult(false, $"Fix {names} not programmed — use DCTF to override");
                }
            }
        }

        string fixNames = FixListSpaced(cmd.Fixes);

        // A plain direct-to turns the shortest way to the fix. Clear any forced turn
        // direction left over from a preceding relative turn (RELR/RELL/RT/LT) so the
        // stale bias does not steer the aircraft the long way around (TLDCT/TRDCT set it
        // intentionally via ApplyTurnDirectTo).
        aircraft.Targets.PreferredTurnDirection = null;

        if (cmd.Fixes.Count == 1 && TryPreserveProcedure(aircraft, cmd.Fixes[0].Name))
        {
            aircraft.Targets.AssignedMagneticHeading = null;
            return CommandDispatcher.Ok($"Proceed direct {fixNames}");
        }

        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.AssignedMagneticHeading = null;
        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.FlightPlan.Route);
        foreach (ResolvedFix? fix in resolved)
        {
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
        }
        bool routeRejoined = resolved.Count > originalCount;
        return routeRejoined
            ? CommandDispatcher.Ok($"Proceed direct {fixNames}, then filed route")
            : CommandDispatcher.Ok($"Proceed direct {fixNames}");
    }

    internal static CommandResult ApplyTurnDirectTo(List<ResolvedFix> fixes, AircraftState aircraft, bool validateDctFixes, TurnDirection direction)
    {
        if (validateDctFixes)
        {
            HashSet<string> programmed = aircraft.GetProgrammedFixes();
            if (programmed.Count > 0)
            {
                var unprogrammed = fixes.Where(f => !programmed.Contains(f.Name)).ToList();
                if (unprogrammed.Count > 0)
                {
                    string names = FixListCommas(unprogrammed);
                    return new CommandResult(false, $"Fix {names} not programmed — use DCTF to override");
                }
            }
        }

        string dirLabel = direction == TurnDirection.Left ? "Turn left, direct" : "Turn right, direct";
        string fixNames = FixListSpaced(fixes);

        if (fixes.Count == 1 && TryPreserveProcedure(aircraft, fixes[0].Name))
        {
            aircraft.Targets.AssignedMagneticHeading = null;
            aircraft.Targets.PreferredTurnDirection = direction;
            return CommandDispatcher.Ok($"{dirLabel} {fixNames}");
        }

        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Targets.PreferredTurnDirection = direction;
        var resolved = fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.FlightPlan.Route);
        foreach (ResolvedFix? fix in resolved)
        {
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
        }
        bool routeRejoined = resolved.Count > originalCount;
        return routeRejoined ? CommandDispatcher.Ok($"{dirLabel} {fixNames}, then filed route") : CommandDispatcher.Ok($"{dirLabel} {fixNames}");
    }

    internal static CommandResult ApplyForceDirectTo(ForceDirectToCommand cmd, AircraftState aircraft)
    {
        string fixNames = FixListSpaced(cmd.Fixes);

        // Clear any forced turn direction left over from a preceding relative turn so a
        // plain direct-to turns the shortest way to the fix (see ApplyDirectTo).
        aircraft.Targets.PreferredTurnDirection = null;

        if (cmd.Fixes.Count == 1 && TryPreserveProcedure(aircraft, cmd.Fixes[0].Name))
        {
            aircraft.Targets.AssignedMagneticHeading = null;
            return CommandDispatcher.Ok($"Proceed direct {fixNames}");
        }

        ClearActiveProcedure(aircraft);
        NavigationCommandHandler.ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.AssignedMagneticHeading = null;
        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.FlightPlan.Route);
        foreach (ResolvedFix? fix in resolved)
        {
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
        }
        bool routeRejoined = resolved.Count > originalCount;
        return routeRejoined
            ? CommandDispatcher.Ok($"Proceed direct {fixNames}, then filed route")
            : CommandDispatcher.Ok($"Proceed direct {fixNames}");
    }

    /// <summary>
    /// Flies direct to <paramref name="cmd"/>'s fixes with its crossing restrictions. A fix whose altitude the command
    /// sets takes the transmission's crossing stamp, and one whose speed it sets the speed stamp, so a DVIA in the same
    /// transmission keeps the crossing and a later one supersedes it; a restriction it does not set keeps its stamp and its
    /// reverts. A crossing speed the type can fly neither at its present altitude nor at that fix's crossing altitude
    /// (or, with none, its assigned altitude) is refused before anything changes, as CFIX's is
    /// (<see cref="RefuseUnflyableSpeed"/>); an accepted crossing speed is a controller speed act and ends an SPDN's lift
    /// (<see cref="ControlTargets.SpeedLimitsLifted"/>).
    /// <para>Fixes all on the active STAR's route, in order, keep the STAR from the first of them, with the crossings on
    /// its fixes (<see cref="KeepStarForConstrainedDirect"/>), so "descend via, proceed direct X, cross X at or above …"
    /// in one transmission, either order, flies the rest of the STAR (7110.65 4-5-7.h; AIM 5-4-1.a.2).</para>
    /// </summary>
    internal static CommandResult ApplyConstrainedForceDirectTo(ConstrainedForceDirectToCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        // Crossing speeds are speed assignments, refused for an arrival on final inside 5 nm as a plain SPD is
        // (7110.65 §5-7-1.b.4); altitudes alone are an altitude instruction and are accepted.
        if ((cmd.SpeedConstraints is { Count: > 0 }) && (RefuseInsideFinalGate(aircraft) is { } insideFinal))
        {
            return insideFinal;
        }

        if (RefuseUnflyableCrossingSpeed(cmd, aircraft, ctx) is { } unflyable)
        {
            return unflyable;
        }

        int transmissionId = ctx.TransmissionId;

        // The pending revert of every dropped crossing applies now, as other route changes apply it, before anything
        // below reads the speed state. With no crossing speed here that is the end of it; with one, the speed it restored
        // (a plain speed, a Mach hold or SMAX armed again) is what this direct-to's last speed fix reverts to, so the
        // dropped crossing's revert moves past the new crossing rather than being lost. A Mach restored as an IAS below
        // the handover altitude and an RNS, RPS or DSR leave no live state that says so, so they are read from the fixes
        // taken: each is what the dropped fix restored when neither SMAX nor (for the resume) a Mach came first there.
        StarCrossingFixes? starFixes = StarFixesForConstrainedDirect(cmd, aircraft);
        (
            List<NavigationTarget> taken,
            List<NavigationTarget> speedStamped,
            bool endsVia,
            bool restatedWithVia,
            List<NavigationTarget> abandonedPending
        ) = TakeRouteForConstrainedDirect(cmd, aircraft, starFixes, transmissionId);

        bool speedMaxPending = NavigationCommandHandler.SpeedMaxPending(aircraft, aircraft.Targets.NavigationRoute);
        NavigationTarget? takenMachHolder = taken.FindLast(fix => !fix.RevertSpeedMax && (fix.RevertMach is not null));
        SpeedResumeKind? takenResume = taken
            .FindLast(fix => !fix.RevertSpeedMax && (fix.RevertMach is null) && (fix.RevertResume is not null))
            ?.RevertResume;

        aircraft.Targets.AssignedMagneticHeading = null;
        // Clear any forced turn direction left over from a preceding relative turn so a
        // plain direct-to turns the shortest way to the fix (see ApplyDirectTo).
        aircraft.Targets.PreferredTurnDirection = null;

        // The last index that has an altitude constraint carries the altitude revert.
        int lastConstrainedIdx = (cmd.AltitudeConstraints.Count > 0) ? cmd.AltitudeConstraints.Keys.Max() : -1;

        int lastSpeedIdx = (cmd.SpeedConstraints is { Count: > 0 } speeds) ? speeds.Keys.Max() : -1;
        (double? machToRestore, bool? machToRestoreExplicit) = ConstrainedDirectMachToRestore(
            aircraft,
            speedStamped,
            takenMachHolder,
            speedMaxPending || restatedWithVia
        );

        (double? revertAlt, double? revertAssignedAlt) = CaptureConstrainedDirectAltitude(
            cmd,
            aircraft,
            lastConstrainedIdx,
            endsVia,
            restatedWithVia
        );

        var crossing = new ConstrainedDirectCrossing(
            cmd,
            transmissionId,
            lastConstrainedIdx,
            lastSpeedIdx,
            revertAlt,
            revertAssignedAlt,
            machToRestore,
            machToRestoreExplicit,
            restatedWithVia ? null : aircraft.Targets.TargetSpeed,
            restatedWithVia ? null : aircraft.Targets.AssignedSpeed,
            speedMaxPending
        );

        NavigationCommandHandler.PendingSpeedReverts pending = ClearTakenAndReadPendingSpeedReverts(
            aircraft,
            [.. speedStamped, .. abandonedPending],
            takenResume
        );
        StampConstrainedDirectFixes(aircraft, starFixes, crossing);
        if (cmd.SpeedConstraints is { Count: > 0 })
        {
            PlaceConstrainedDirectSpeedReverts(aircraft, crossing, starFixes is not null, restatedWithVia, pending);
        }

        string fixNames = FixListSpaced(cmd.Fixes);
        return CommandDispatcher.Ok($"Proceed direct {fixNames}");
    }

    /// <summary>
    /// The Mach a constrained direct-to's crossing speeds replace, with whether ATC assigned it: held now or restored from
    /// a dropped crossing (<paramref name="takenMachHolder"/>), it is restored past the last fix whose speed the direct-to
    /// sets, altitude-constrained or not, and then no fix carries a plain speed revert. None when <paramref name="noRestore"/>
    /// (SMAX is pending, whose re-arm PlaceSpeedMaxRevert places instead, or the crossing is restated with a via). A Mach
    /// still held after an earlier crossing captured it, whose revert a kept fix outside <paramref name="speedStamped"/>
    /// still holds, is that revert's Mach: it moves past the direct-to's crossings with the pending revert rather than
    /// being captured a second time.
    /// </summary>
    private static (double? Mach, bool? MachExplicit) ConstrainedDirectMachToRestore(
        AircraftState aircraft,
        List<NavigationTarget> speedStamped,
        NavigationTarget? takenMachHolder,
        bool noRestore
    )
    {
        double? liveMach = aircraft.Targets.NavigationRoute.Exists(fix => (fix.RevertMach is not null) && !speedStamped.Contains(fix))
            ? null
            : aircraft.Targets.TargetMach;
        double? mach = noRestore ? null : (liveMach ?? takenMachHolder?.RevertMach);
        bool? machExplicit = (liveMach is not null) ? aircraft.Targets.HasExplicitSpeedCommand : takenMachHolder?.RevertMachExplicit;
        return (mach, machExplicit);
    }

    /// <summary>
    /// Captures the altitude a constrained direct-to reverts to past its last altitude-constrained fix
    /// (<paramref name="lastConstrainedIdx"/>), or assigns that fix's crossing altitude when it reverts to none.
    /// Nothing is captured when reverting would reverse the direction flown into the fix
    /// (<see cref="NavigationCommandHandler.AltitudeRevertReversesTravel"/>) or the crossing is restated with this
    /// transmission's via (<paramref name="restatedWithVia"/>). When this direct ended a via (<paramref name="endsVia"/>),
    /// the via's computed target is no clearance (7110.65 4-5-7.h.4) and the crossing authorizes the descent only to the
    /// fix (AIM 4-4-10.e), so only an assigned altitude is gone back to; with none, the crossing altitude stays assigned
    /// past the fix (AIM 5-4-1.a.2; 4-4-10.e, 4-4-10.g).
    /// </summary>
    private static (double? PreviousAltitude, double? PreviousAssignedAltitude) CaptureConstrainedDirectAltitude(
        ConstrainedForceDirectToCommand cmd,
        AircraftState aircraft,
        int lastConstrainedIdx,
        bool endsVia,
        bool restatedWithVia
    )
    {
        double? revertTarget = endsVia ? aircraft.Targets.AssignedAltitude : aircraft.Targets.TargetAltitude;
        bool revertsAltitude =
            (lastConstrainedIdx >= 0)
            && !restatedWithVia
            && (!endsVia || (revertTarget is not null))
            && !NavigationCommandHandler.AltitudeRevertReversesTravel(
                aircraft.Altitude,
                cmd.AltitudeConstraints[lastConstrainedIdx].AltitudeFt,
                revertTarget
            );
        double? previousAlt = revertsAltitude ? revertTarget : null;
        double? previousAssignedAlt = revertsAltitude ? aircraft.Targets.AssignedAltitude : null;

        // Without a revert the last crossing altitude stays assigned past its fix (AIM 4-4-10.g), as with CFIX, unless the
        // cleared altitude already meets its bound (NavigationCommandHandler.CrossingBoundMetByClearance) or the crossing is
        // restated with this transmission's via, which cleared the assigned altitude
        if (
            (lastConstrainedIdx >= 0)
            && !revertsAltitude
            && !restatedWithVia
            && !NavigationCommandHandler.CrossingBoundMetByClearance(
                aircraft,
                ToAltitudeRestriction(cmd.AltitudeConstraints[lastConstrainedIdx].AltitudeFt, cmd.AltitudeConstraints[lastConstrainedIdx].AltType)
            )
        )
        {
            aircraft.Targets.AssignedAltitude = cmd.AltitudeConstraints[lastConstrainedIdx].AltitudeFt;
        }

        return (previousAlt, previousAssignedAlt);
    }

    /// <summary>
    /// Takes what a constrained direct-to drops before it captures the speed state: the speed reverts of the pending
    /// departure route it abandons (a crossing given in a SID's climb window), applied with the live route's, and either
    /// the fixes before an active STAR it keeps (<paramref name="starFixes"/>, <see cref="KeepStarForConstrainedDirect"/>)
    /// or the whole live route, which it clears ending the procedure. Returns every holder taken and applied, the kept STAR
    /// fixes whose speed the direct-to sets, whether the STAR ends in a via, whether a DVIA of this transmission restates
    /// the crossing, and the abandoned pending route's holders, which the caller clears once its reads are done so the
    /// departure phase's own abandonment finds none left.
    /// </summary>
    private static (
        List<NavigationTarget> Taken,
        List<NavigationTarget> SpeedStamped,
        bool EndsVia,
        bool RestatedWithVia,
        List<NavigationTarget> AbandonedPending
    ) TakeRouteForConstrainedDirect(ConstrainedForceDirectToCommand cmd, AircraftState aircraft, StarCrossingFixes? starFixes, int transmissionId)
    {
        List<NavigationTarget> abandonedPending = NavigationCommandHandler.PendingDepartureRoute(aircraft) is { } pendingRoute
            ? NavigationCommandHandler.TakePendingSpeedReverts(pendingRoute)
            : [];
        if (starFixes is not null)
        {
            NavigationCommandHandler.ApplyTakenSpeedReverts(aircraft, abandonedPending);
            // Fixes consecutive on the active STAR's route, in order, keep the STAR from the first of them (7110.65
            // 4-5-7.h; AIM 5-4-1.a.2): the fixes before it are dropped, and the crossings go onto the STAR's own fixes.
            List<NavigationTarget> speedStamped = SpeedStampedStarFixes(cmd, starFixes);
            List<NavigationTarget> dropped = KeepStarForConstrainedDirect(aircraft, starFixes, speedStamped, transmissionId, out bool endsVia);
            List<NavigationTarget> taken = [.. abandonedPending, .. dropped];
            // After a DVIA of this same transmission has applied, the crossing is restated with it, as a CFIX is: no reverts,
            // and the assigned altitude the via cleared is left alone. Read once every other via has ended (a CVIA too).
            bool restatedWithVia = NavigationCommandHandler.ViaAppliedInTransmission(aircraft, transmissionId);
            return (taken, speedStamped, endsVia, restatedWithVia, abandonedPending);
        }

        // Any other fixes replace the route, and the procedure ends.
        ClearActiveProcedure(aircraft);
        List<NavigationTarget> replaced =
        [
            .. abandonedPending,
            .. NavigationCommandHandler.TakePendingSpeedReverts(aircraft.Targets.NavigationRoute),
        ];
        aircraft.Targets.NavigationRoute.Clear();
        NavigationCommandHandler.ApplyTakenSpeedReverts(aircraft, replaced);
        return (replaced, [], false, false, abandonedPending);
    }

    /// <summary>
    /// Clears the speed reverts of holders a constrained direct-to took and applied already (<paramref name="applied"/>:
    /// the kept fixes whose speed it sets and the abandoned pending departure route's holders), so they do not restore
    /// again when crossed or abandoned. Called only once the reads of what was taken (the Mach, the resume) are done.
    /// Returns the reverts still pending on the route, read after the clear: those, on kept fixes whose speed the
    /// direct-to leaves alone, move past its last crossing speed as a CFIX moves them, and a resume taken from a dropped
    /// or restated crossing (<paramref name="takenResume"/>) is carried the same way.
    /// </summary>
    private static NavigationCommandHandler.PendingSpeedReverts ClearTakenAndReadPendingSpeedReverts(
        AircraftState aircraft,
        List<NavigationTarget> applied,
        SpeedResumeKind? takenResume
    )
    {
        foreach (NavigationTarget holder in applied)
        {
            NavigationCommandHandler.ClearSpeedReverts(holder);
        }

        var pending = NavigationCommandHandler.PendingSpeedReverts.Read(aircraft.Targets.NavigationRoute);
        return pending with { Resume = takenResume ?? pending.Resume };
    }

    /// <summary>
    /// Writes a constrained direct-to's crossings (<see cref="StampConstrainedDirectFix"/>): onto new fixes appended to the
    /// emptied route when it replaces the route (<paramref name="starFixes"/> null), else onto the kept STAR fixes it
    /// constrains.
    /// </summary>
    private static void StampConstrainedDirectFixes(AircraftState aircraft, StarCrossingFixes? starFixes, ConstrainedDirectCrossing crossing)
    {
        ConstrainedForceDirectToCommand cmd = crossing.Command;
        for (int i = 0; i < cmd.Fixes.Count; i++)
        {
            if (starFixes is null)
            {
                ResolvedFix fix = cmd.Fixes[i];
                var target = new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) };
                StampConstrainedDirectFix(target, i, crossing);
                aircraft.Targets.NavigationRoute.Add(target);
            }
            else if (starFixes.Stamped[i] is { } starFix)
            {
                StampConstrainedDirectFix(starFix, i, crossing);
            }
        }
    }

    /// <summary>
    /// After a constrained direct-to with crossing speeds has stamped its fixes, leaves the one speed revert on the route
    /// and ends what a speed act ends. Restated with this transmission's via (<paramref name="restatedWithVia"/>), the
    /// crossing speed stays in force and is the assigned speed; under SMAX its re-arm sits past the last crossing speed;
    /// kept on the STAR (<paramref name="keepsStar"/>) the revert is placed as a CFIX places it
    /// (<see cref="NavigationCommandHandler.PlaceCrossingSpeedReverts"/>); on a replaced route a resume taken from a
    /// dropped crossing, carried in <paramref name="pending"/>, is carried past the last crossing speed.
    /// </summary>
    private static void PlaceConstrainedDirectSpeedReverts(
        AircraftState aircraft,
        ConstrainedDirectCrossing crossing,
        bool keepsStar,
        bool restatedWithVia,
        NavigationCommandHandler.PendingSpeedReverts pending
    )
    {
        if (restatedWithVia)
        {
            // Restated with this transmission's via, the crossing speed stays in force and is the assigned speed, as
            // when the via comes second and cancels the speeds before it.
            NavigationCommandHandler.KeepTransmissionCrossingSpeed(aircraft, aircraft.Targets.NavigationRoute, crossing.TransmissionId);
        }
        else if (crossing.SpeedMaxPending)
        {
            NavigationCommandHandler.PlaceSpeedMaxRevert(aircraft.Targets.NavigationRoute);
        }
        else if (keepsStar)
        {
            // Kept on the STAR, the one speed revert sits past the last controller-set crossing speed on the route,
            // as a CFIX places it (NavigationCommandHandler.PlaceCrossingSpeedReverts).
            var capture = new NavigationCommandHandler.SpeedRevertCapture(
                SpeedMax: false,
                Mach: crossing.MachToRestore,
                MachExplicit: crossing.MachToRestoreExplicit,
                Plain: crossing.MachToRestore is null,
                Speed: crossing.TargetSpeed,
                AssignedSpeed: crossing.AssignedSpeed,
                Floor: null,
                Ceiling: null
            );
            NavigationCommandHandler.PlaceCrossingSpeedReverts(aircraft.Targets.NavigationRoute, capture, pending, speedRestated: false);
        }
        else if ((crossing.MachToRestore is null) && (pending.Resume is { } resume))
        {
            // The RNS, RPS or DSR given with a dropped crossing is done past this direct-to's last crossing speed,
            // as a CFIX carries it, so that crossing's ceiling does not hold for good.
            NavigationCommandHandler.PlaceResumeRevert(aircraft.Targets.NavigationRoute, resume);
        }

        aircraft.Targets.SpeedLimitsLifted = false;
        // A crossing speed is an ATC speed act like SPD, so it ends a forced speed's override of the final gate.
        aircraft.Targets.SpeedOverridesFinalGate = false;
        EndSpeedMaxHold(aircraft);
    }

    /// <summary>
    /// What a constrained direct-to writes on its fixes, read once before the first: the command, its transmission, the
    /// last altitude- and speed-constrained indexes, the altitude revert, the Mach to restore, the plain speed to revert
    /// to and whether SMAX is pending.
    /// </summary>
    private sealed record ConstrainedDirectCrossing(
        ConstrainedForceDirectToCommand Command,
        int TransmissionId,
        int LastAltitudeIdx,
        int LastSpeedIdx,
        double? PreviousAltitude,
        double? PreviousAssignedAltitude,
        double? MachToRestore,
        bool? MachToRestoreExplicit,
        double? TargetSpeed,
        double? AssignedSpeed,
        bool SpeedMaxPending
    );

    /// <summary>
    /// Writes fix <paramref name="index"/>'s crossing of a constrained direct-to onto <paramref name="target"/>: the
    /// altitude and speed restrictions the command sets, each with its transmission stamp, the altitude revert on the
    /// last altitude-constrained fix, and the Mach or plain speed revert on the last speed fix. A restriction the command
    /// does not set stays as the fix has it: none on a new fix of the replacement route, the published one on a fix kept
    /// on the active STAR, whose remainder is flown as published (7110.65 4-5-7.h.9 NOTE; AIM 5-4-1.a.2(a) NOTE 1).
    /// </summary>
    private static void StampConstrainedDirectFix(NavigationTarget target, int index, ConstrainedDirectCrossing crossing)
    {
        ConstrainedForceDirectToCommand cmd = crossing.Command;
        CifpAltitudeRestriction? altRestriction = target.AltitudeRestriction;
        CifpSpeedRestriction? speedRestriction = target.SpeedRestriction;
        bool setsAltitude = false;
        bool setsSpeed = false;
        double? revertAlt = null;
        double? revertAssignedAlt = null;
        double? revertMach = null;
        bool? revertMachExplicit = null;
        double? revertSpeed = null;
        double? revertAssignedSpeed = null;

        if (cmd.AltitudeConstraints.TryGetValue(index, out ConstrainedFixAltitude? alt))
        {
            altRestriction = ToAltitudeRestriction(alt.AltitudeFt, alt.AltType);
            setsAltitude = true;

            // Only the last constrained fix gets the altitude revert
            if (index == crossing.LastAltitudeIdx)
            {
                revertAlt = crossing.PreviousAltitude;
                revertAssignedAlt = crossing.PreviousAssignedAltitude;
            }
        }

        if ((cmd.SpeedConstraints is not null) && cmd.SpeedConstraints.TryGetValue(index, out int spd))
        {
            speedRestriction = new CifpSpeedRestriction(spd, CifpSpeedRestrictionType.AtOrBelow);
            setsSpeed = true;
            if ((index == crossing.LastSpeedIdx) && (crossing.MachToRestore is not null))
            {
                revertMach = crossing.MachToRestore;
                revertMachExplicit = crossing.MachToRestoreExplicit;
            }

            // A plain speed revert sits on the last fix whose speed this direct-to sets, altitude-constrained or
            // not, as the Mach revert does; the altitude revert alone sits on the last constrained fix.
            if ((index == crossing.LastSpeedIdx) && !crossing.SpeedMaxPending && (crossing.MachToRestore is null))
            {
                revertSpeed = crossing.TargetSpeed;
                revertAssignedSpeed = crossing.AssignedSpeed;
            }
        }

        target.AltitudeRestriction = altRestriction;
        target.SpeedRestriction = speedRestriction;
        // Each stamp marks what this transmission set: the crossing stamp the altitude, the speed stamp the speed. A
        // restriction the command leaves alone keeps its stamp and the reverts an earlier crossing left with it, so an
        // altitude-only direct keeps the fix's speed reverts and a speed-only one its altitude reverts. Id 0 is no
        // transmission (a queued block restored from a snapshot written before blocks carried one).
        int? stamp = (crossing.TransmissionId != 0) ? crossing.TransmissionId : null;
        if (setsAltitude)
        {
            target.RevertAltitude = revertAlt;
            target.RevertAssignedAltitude = revertAssignedAlt;
            target.CrossingTransmissionId = stamp;
        }

        if (setsSpeed)
        {
            target.RevertSpeed = revertSpeed;
            target.RevertAssignedSpeed = revertAssignedSpeed;
            target.RevertMach = revertMach;
            target.RevertMachExplicit = revertMachExplicit;
            target.SpeedTransmissionId = stamp;
        }
    }

    /// <summary>
    /// Where a constrained direct-to keeps the active STAR (<see cref="StarCrossingFixes"/>), or null when the route is
    /// replaced instead: no STAR is active, or the command fixes are not consecutive route fixes in order (names compared
    /// case-insensitively), as a plain direct keeps the procedure only for the one fix it names.
    /// </summary>
    private static StarCrossingFixes? StarFixesForConstrainedDirect(ConstrainedForceDirectToCommand cmd, AircraftState aircraft)
    {
        if ((aircraft.Procedure.ActiveStarId is null) || (cmd.Fixes.Count == 0))
        {
            return null;
        }

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        string firstName = cmd.Fixes[0].Name;
        int firstIndex = route.FindIndex(f => string.Equals(f.Name, firstName, StringComparison.OrdinalIgnoreCase));
        if ((firstIndex < 0) || (firstIndex + cmd.Fixes.Count > route.Count))
        {
            return null;
        }

        var fixes = new NavigationTarget?[cmd.Fixes.Count];
        for (int i = 0; i < cmd.Fixes.Count; i++)
        {
            NavigationTarget routeFix = route[firstIndex + i];
            if (!string.Equals(routeFix.Name, cmd.Fixes[i].Name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            bool constrained = cmd.AltitudeConstraints.ContainsKey(i) || (cmd.SpeedConstraints?.ContainsKey(i) == true);
            fixes[i] = constrained ? routeFix : null;
        }

        return new StarCrossingFixes(firstIndex, fixes);
    }

    /// <summary>
    /// Keeps the active STAR for a constrained direct-to onto its fixes: the route fixes before the first command fix go,
    /// and the speed reverts they held, with those held by the fixes whose speed the command is about to set
    /// (<paramref name="speedStamped"/>), apply now with the STAR still active (<see cref="ApplySkippedSpeedReverts"/>);
    /// the STAR, its runway and the route from the first command fix on stay. A descend via earlier in this transmission
    /// (<paramref name="transmissionId"/>) stays in force; any other via ends, as a direct onto the procedure ends it
    /// (<see cref="TryPreserveProcedure"/>). Returns the fixes whose speed reverts were taken, the speed-stamped ones still
    /// holding them, so the caller reads what they restored before it clears them; <paramref name="endsVia"/> is whether
    /// a via was in force and this direct ended it.
    /// </summary>
    private static List<NavigationTarget> KeepStarForConstrainedDirect(
        AircraftState aircraft,
        StarCrossingFixes starFixes,
        List<NavigationTarget> speedStamped,
        int transmissionId,
        out bool endsVia
    )
    {
        endsVia = false;
        if ((transmissionId == 0) || (aircraft.Procedure.StarViaTransmissionId != transmissionId))
        {
            endsVia = aircraft.Procedure.StarViaMode || aircraft.Procedure.SidViaMode;
            EndViaModes(aircraft);
        }

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        List<NavigationTarget> taken = NavigationCommandHandler.TakePendingSpeedReverts(route.GetRange(0, starFixes.FirstIndex));
        route.RemoveRange(0, starFixes.FirstIndex);
        List<NavigationTarget> stampedHolders = NavigationCommandHandler.TakePendingSpeedReverts(speedStamped);
        taken.AddRange(stampedHolders);
        ApplySkippedSpeedReverts(aircraft, taken);
        return taken;
    }

    /// <summary>The kept STAR fixes whose speed <paramref name="cmd"/> sets, in command order.</summary>
    private static List<NavigationTarget> SpeedStampedStarFixes(ConstrainedForceDirectToCommand cmd, StarCrossingFixes starFixes)
    {
        List<NavigationTarget> fixes = [];
        for (int i = 0; i < starFixes.Stamped.Length; i++)
        {
            if ((starFixes.Stamped[i] is { } fix) && (cmd.SpeedConstraints?.ContainsKey(i) == true))
            {
                fixes.Add(fix);
            }
        }

        return fixes;
    }

    /// <summary>Ends every via clearance in force, keeping the procedure ids (a direct-to onto the active procedure).</summary>
    private static void EndViaModes(AircraftState aircraft)
    {
        aircraft.Procedure.SidViaMode = false;
        aircraft.Procedure.ClimbViaTopFromTdls = false;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.SidViaCeiling = null;
        aircraft.Procedure.ClimbViaTargetFt = null;
        aircraft.Procedure.ClimbViaTransmissionId = null;
        aircraft.Procedure.StarViaFloor = null;
    }

    /// <summary>
    /// Where a constrained direct-to that keeps the active STAR meets it: the route index of its first fix, and the route
    /// fix each command fix stamps (null where the command constrains that fix neither in altitude nor in speed).
    /// </summary>
    private sealed record StarCrossingFixes(int FirstIndex, NavigationTarget?[] Stamped);

    /// <summary>
    /// The unable for the first crossing speed in <paramref name="cmd"/> the type can fly neither here nor at the fix's
    /// crossing altitude (its assigned altitude when the fix has none), or null when every one is flyable.
    /// </summary>
    private static CommandResult? RefuseUnflyableCrossingSpeed(ConstrainedForceDirectToCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        if (cmd.SpeedConstraints is null)
        {
            return null;
        }

        foreach ((int index, int speed) in cmd.SpeedConstraints)
        {
            double? otherAltitude = cmd.AltitudeConstraints.TryGetValue(index, out ConstrainedFixAltitude? crossing)
                ? crossing.AltitudeFt
                : aircraft.Targets.AssignedAltitude;
            if (RefuseUnflyableSpeed(aircraft, ctx, speed, SpeedModifier.None, otherAltitude) is { } unflyable)
            {
                return unflyable;
            }
        }

        return null;
    }

    /// <summary>The crossing restriction a CFIX or a constrained DCTF fix stamps for <paramref name="altType"/>.</summary>
    internal static CifpAltitudeRestriction ToAltitudeRestriction(int altitudeFt, CrossFixAltitudeType altType)
    {
        CifpAltitudeRestrictionType restrictionType = altType switch
        {
            CrossFixAltitudeType.AtOrAbove => CifpAltitudeRestrictionType.AtOrAbove,
            CrossFixAltitudeType.AtOrBelow => CifpAltitudeRestrictionType.AtOrBelow,
            _ => CifpAltitudeRestrictionType.At,
        };
        return new CifpAltitudeRestriction(restrictionType, altitudeFt) { Source = CifpAltitudeRestrictionSource.Atc };
    }

    internal static CommandResult ApplyAppendDirectTo(AppendDirectToCommand cmd, AircraftState aircraft, bool validateDctFixes)
    {
        if (validateDctFixes)
        {
            HashSet<string> programmed = aircraft.GetProgrammedFixes();
            if (programmed.Count > 0)
            {
                var unprogrammed = cmd.Fixes.Where(f => !programmed.Contains(f.Name)).ToList();
                if (unprogrammed.Count > 0)
                {
                    string badNames = FixListCommas(unprogrammed);
                    return new CommandResult(false, $"Fix {badNames} not programmed — use DCTF to override");
                }
            }
        }

        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.FlightPlan.Route);
        // A route holding only the clearance-limit direct has run out: the fixes replace the direct rather than follow it.
        bool routeRanOut = !aircraft.Targets.HasRouteToFly;
        aircraft.Targets.StripClearanceLimitDirect();
        if (routeRanOut)
        {
            foreach (ResolvedFix? fix in resolved)
            {
                aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
            }
            string names = FixListSpaced(cmd.Fixes);
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Proceed direct {names}, then filed route")
                : CommandDispatcher.Ok($"Proceed direct {names}");
        }
        else
        {
            foreach (ResolvedFix? fix in resolved)
            {
                aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
            }
            string appended = FixListSpaced(cmd.Fixes);
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Then direct {appended}, then filed route")
                : CommandDispatcher.Ok($"Then direct {appended}");
        }
    }

    internal static CommandResult ApplyAppendForceDirectTo(AppendForceDirectToCommand cmd, AircraftState aircraft)
    {
        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.FlightPlan.Route);
        // A route holding only the clearance-limit direct has run out: the fixes replace the direct rather than follow it.
        bool routeRanOut = !aircraft.Targets.HasRouteToFly;
        aircraft.Targets.StripClearanceLimitDirect();
        if (routeRanOut)
        {
            foreach (ResolvedFix? fix in resolved)
            {
                aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
            }
            string names = FixListSpaced(cmd.Fixes);
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Proceed direct {names}, then filed route")
                : CommandDispatcher.Ok($"Proceed direct {names}");
        }
        else
        {
            foreach (ResolvedFix? fix in resolved)
            {
                aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
            }
            string appended = FixListSpaced(cmd.Fixes);
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Then direct {appended}, then filed route")
                : CommandDispatcher.Ok($"Then direct {appended}");
        }
    }

    internal static CommandResult ApplySetTurnRate(SetTurnRateCommand cmd, AircraftState aircraft)
    {
        aircraft.Targets.TurnRateOverride = cmd.DegreesPerSecond;
        aircraft.Targets.HasExplicitTurnRate = true;
        return CommandDispatcher.Ok($"Turn rate set to {cmd.DegreesPerSecond} deg/sec");
    }

    internal static CommandResult ApplyClearTurnRate(AircraftState aircraft)
    {
        aircraft.Targets.TurnRateOverride = null;
        aircraft.Targets.HasExplicitTurnRate = false;
        return CommandDispatcher.Ok("Turn rate reset to default");
    }

    internal static CommandResult ApplyWarp(WarpCommand cmd, AircraftState aircraft)
    {
        MagneticHeading heading = cmd.MagneticHeading ?? aircraft.MagneticHeading;
        int altitude = cmd.Altitude ?? (int)Math.Round(aircraft.Altitude);
        int speed = cmd.Speed ?? (int)Math.Round(aircraft.IndicatedAirspeed);

        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        if (aircraft.Phases is not null)
        {
            // No route to the room's active runways: this handler is not given the dispatch context.
            PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
            aircraft.Phases.Clear(ctx);
            TugReleasePhase.StopDead(aircraft);
            aircraft.Phases = null;
        }
        aircraft.Ground.AssignedTaxiRoute = null;
        aircraft.Ground.TaxiEdgeTrail.Clear();
        aircraft.Targets.TurnRateOverride = null;
        aircraft.Targets.HasExplicitTurnRate = false;
        aircraft.Position = new LatLon(cmd.Latitude, cmd.Longitude);
        aircraft.TrueHeading = heading.ToTrue(aircraft.Declination);
        aircraft.TrueTrack = heading.ToTrue(aircraft.Declination);
        aircraft.Altitude = altitude;
        aircraft.VerticalSpeed = 0;
        aircraft.IndicatedAirspeed = speed;
        aircraft.Targets.TargetTrueHeading = heading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = heading;
        aircraft.Targets.PreferredTurnDirection = null;
        aircraft.Targets.TargetAltitude = altitude;
        aircraft.Targets.AltitudeFloor = null;
        aircraft.Targets.AltitudeCeiling = null;
        aircraft.Targets.AssignedAltitude = altitude;
        aircraft.Targets.TargetSpeed = speed;
        aircraft.Targets.AssignedSpeed = speed;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.SpeedLimitsLifted = false;
        EndMachHoldAndSpeedMax(aircraft);
        aircraft.Procedure.LastProcedureSpeedKts = null;
        aircraft.IsOnGround = false;
        aircraft.Situation.WasOnGround = false;
        return CommandDispatcher.Ok($"Warped to {cmd.PositionLabel}, heading {heading.Degrees:000}, {altitude:N0} ft, {speed} kts");
    }

    /// <summary>
    /// Teleports an aircraft onto a node of the ground layout and leaves it idling there.
    /// </summary>
    /// <param name="cmd">The parsed WARPG command naming the target node, stand, spot or intersection.</param>
    /// <param name="aircraft">The aircraft to move.</param>
    /// <param name="groundLayout">The dispatch context's resolved layout. The aircraft's own cached
    /// <c>Ground.Layout</c> is unset for an arrival that landed and taxied, so it is not read here.</param>
    /// <returns>The command result.</returns>
    internal static CommandResult ApplyWarpGround(WarpGroundCommand cmd, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        AirportGroundLayout? layout = groundLayout;
        if (layout is null)
        {
            return new CommandResult(false, "No airport layout loaded for this aircraft");
        }

        GroundNode? node;
        string description;

        if (cmd.NodeId is int nodeId)
        {
            if (!layout.Nodes.TryGetValue(nodeId, out node))
            {
                return new CommandResult(false, $"Node #{nodeId} not found in airport layout");
            }
            description = $"node #{nodeId}" + (node.Name is not null ? $" ({node.Name})" : "");
        }
        else if (cmd.ParkingName is string parkingName)
        {
            node = layout.FindSpotByName(parkingName);
            if (node is null)
            {
                return new CommandResult(false, $"Parking/spot '{parkingName}' not found in airport layout");
            }
            description = $"parking {parkingName}";
        }
        else if (cmd.SpotName is string spotName)
        {
            node = layout.FindSpotNodeByName(spotName);
            if (node is null)
            {
                return new CommandResult(false, $"Spot '{spotName}' not found in airport layout");
            }
            description = $"spot {spotName}";
        }
        else
        {
            node = CommandDispatcher.FindTaxiwayIntersection(layout, cmd.Taxiway1, cmd.Taxiway2);
            if (node is null)
            {
                return new CommandResult(false, $"No intersection found between {cmd.Taxiway1} and {cmd.Taxiway2}");
            }
            description = $"{cmd.Taxiway1}/{cmd.Taxiway2} intersection";
        }

        // Clear stale state from prior operations
        if (aircraft.Phases is not null)
        {
            // No route to the room's active runways: this handler is not given the dispatch context.
            PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
            aircraft.Phases.Clear(ctx);
            TugReleasePhase.StopDead(aircraft);
            aircraft.Phases = null;
        }
        aircraft.Ground.AssignedTaxiRoute = null;
        aircraft.Ground.TaxiEdgeTrail.Clear();
        aircraft.Queue.Blocks.Clear();
        aircraft.Ground.Hold = null;
        aircraft.Targets.TurnRateOverride = null;
        aircraft.Targets.HasExplicitTurnRate = false;

        // Place on ground at the target node with best heading alignment
        aircraft.Position = node.Position;
        aircraft.IndicatedAirspeed = 0;
        aircraft.IsOnGround = true;
        aircraft.Targets.TargetSpeed = 0;

        // A gate or helipad is a stand: the aircraft rests on the stand's nose-in heading, occupies it,
        // and is not a fresh spawn, so it neither auto-deletes as a parked arrival nor makes a
        // ready-to-taxi call. Anything else is a surface position the aircraft merely idles on, leaving
        // no stand behind. The branch is on the node's type, not on which argument named it: '@' falls
        // back to a spot node and '#' can name a parking node.
        bool atStand = node.Type is GroundNodeType.Parking or GroundNodeType.Helipad;

        TrueHeading heading = atStand && node.TrueHeading is { } standHeading ? standHeading : PickBestEdgeHeading(node, aircraft.TrueHeading);
        aircraft.TrueHeading = heading;
        aircraft.TrueTrack = heading;

        aircraft.Ground.ParkingSpot = atStand ? node.Name : null;
        if (atStand)
        {
            aircraft.Ground.AutoDeleteExempt = true;
            aircraft.Ground.InitialCallupDecisionProcessed = true;
        }

        // Install ground-idle phase so subsequent commands (TAXI, LUAW, PUSH, etc.) have phase context
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(atStand ? new AtParkingPhase() : new HoldingInPositionPhase());
        // No route to the room's active runways: this handler is not given the dispatch context.
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty));

        return CommandDispatcher.Ok($"Warped to {description}");
    }

    internal static void LevelOff(AircraftState aircraft)
    {
        aircraft.Targets.TargetAltitude = null;
        aircraft.Targets.DesiredVerticalRate = null;
        aircraft.Procedure.IsExpediting = false;
    }

    /// <summary>
    /// <paramref name="target"/> as a fix the controller cleared: a controller direct to the clearance-limit direct's
    /// airport makes it an ordinary fix (<see cref="NavigationTarget.IsClearanceLimitDirect"/> false). Any other target is
    /// returned as it is.
    /// </summary>
    private static NavigationTarget AsClearedFix(NavigationTarget target) =>
        target.IsClearanceLimitDirect ? new NavigationTarget { Name = target.Name, Position = target.Position } : target;

    internal static bool TryPreserveProcedure(AircraftState aircraft, string firstFixName)
    {
        if (aircraft.Procedure.ActiveSidId is null && aircraft.Procedure.ActiveStarId is null)
        {
            return false;
        }

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int matchIndex = -1;
        for (int i = 0; i < route.Count; i++)
        {
            if (string.Equals(route[i].Name, firstFixName, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0)
        {
            return false;
        }

        route[matchIndex] = AsClearedFix(route[matchIndex]);

        // Truncate: remove fixes before the matched one, taking the speed reverts they held first.
        List<NavigationTarget> skippedReverts = [];
        if (matchIndex > 0)
        {
            skippedReverts = NavigationCommandHandler.TakePendingSpeedReverts(route.GetRange(0, matchIndex));
            route.RemoveRange(0, matchIndex);
        }

        // Disable via-mode but keep procedure ID
        EndViaModes(aircraft);

        if (
            aircraft.Procedure.ActiveStarId is { } starId
            && aircraft.Procedure.DestinationRunway is { } destRwy
            && !string.IsNullOrEmpty(aircraft.FlightPlan.Destination)
        )
        {
            CifpStarProcedure? star = NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, starId);
            CifpTransition? transition = star is not null ? NavigationCommandHandler.LookupRunwayTransition(star.RunwayTransitions, destRwy) : null;
            if (star is not null && transition is not null)
            {
                NavigationCommandHandler.RemoveStaleStarRunwayTransitionFixes(aircraft, star, transition);
            }
        }

        MovePendingApproachConnectingFixToDirect(aircraft);
        ApplySkippedSpeedReverts(aircraft, skippedReverts);
        return true;
    }

    /// <summary>
    /// When a direct drops a deferred approach's connecting fix from the route, the aircraft flies an unpublished route to
    /// the direct's fix and keeps its last assigned altitude until established there (AIM 5-4-7.f; 7110.65 4-8-1.a.4), so
    /// the pending clearance's connecting fix becomes the direct's fix, the route's first.
    /// </summary>
    private static void MovePendingApproachConnectingFixToDirect(AircraftState aircraft)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        if (
            (aircraft.Approach.PendingClearance is not { ConnectingFix: { } connectingFix } pending)
            || (route.Count == 0)
            || route.Exists(t => t.Name.Equals(connectingFix, StringComparison.OrdinalIgnoreCase))
        )
        {
            return;
        }

        aircraft.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = pending.Clearance,
            AssignedRunway = pending.AssignedRunway,
            ConnectingFix = route[0].Name,
            LandingClearance = pending.LandingClearance,
        };
    }

    /// <summary>
    /// Applies the speed reverts a direct-to onto the active procedure took from the fixes it skipped, with the procedure
    /// still active, so an RPS resumes its upcoming published speeds (7110.65 4-5-7.h NOTE 1). The last crossed published
    /// speed is not held as the ceiling while they apply: it holds only until ATC assigns a direct route (7110.65 5-7-1.e
    /// NOTE), so it is hidden from the resume and put back after.
    /// </summary>
    private static void ApplySkippedSpeedReverts(AircraftState aircraft, List<NavigationTarget> skippedReverts)
    {
        if (skippedReverts.Count == 0)
        {
            return;
        }

        double? lastCrossedKts = aircraft.Procedure.LastCrossedPublishedSpeedKts;
        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
        NavigationCommandHandler.ApplyTakenSpeedReverts(aircraft, skippedReverts);
        aircraft.Procedure.LastCrossedPublishedSpeedKts = lastCrossedKts;
    }

    private static void ClearActiveProcedure(AircraftState aircraft)
    {
        aircraft.Procedure.ActiveSidId = null;
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.SidViaMode = false;
        aircraft.Procedure.ClimbViaTopFromTdls = false;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.SidViaCeiling = null;
        aircraft.Procedure.ClimbViaTargetFt = null;
        aircraft.Procedure.ClimbViaTransmissionId = null;
        aircraft.Procedure.StarViaFloor = null;
        aircraft.Procedure.DepartureRunway = null;
        aircraft.Procedure.DestinationRunway = null;
        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
    }

    private static string AltitudeVerb(AircraftState aircraft, int targetAltitude) =>
        aircraft.Altitude > targetAltitude ? "Descend and maintain" : "Climb and maintain";

    private static string PreviousAltitude(AircraftState aircraft, int newAltitude)
    {
        if (aircraft.Targets.AssignedAltitude is { } prev && (int)prev != newAltitude)
        {
            return $" (was {(int)prev})";
        }

        return "";
    }

    private static string PreviousLateralGuidance(AircraftState aircraft)
    {
        if (aircraft.Targets.AssignedMagneticHeading is { } prevHdg)
        {
            return $" (was heading {prevHdg.Degrees:000})";
        }

        if (aircraft.Targets.NavigationRoute.Count > 0)
        {
            return $" (was DCT {aircraft.Targets.NavigationRoute[0].Name})";
        }

        if (aircraft.Procedure.ActiveSidId is { } sid)
        {
            return $" (was SID {sid})";
        }

        if (aircraft.Procedure.ActiveStarId is { } star)
        {
            return $" (was STAR {star})";
        }

        return "";
    }

    private static TrueHeading PickBestEdgeHeading(GroundNode node, TrueHeading currentHeading)
    {
        TrueHeading best = currentHeading;
        double bestDelta = 360;

        foreach (IGroundEdge edge in node.Edges)
        {
            var bearing = new TrueHeading(EdgeBearing(node, edge));
            double delta = currentHeading.AbsAngleTo(bearing);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = bearing;
            }
        }

        return best;
    }

    private static double EdgeBearing(GroundNode node, IGroundEdge edge)
    {
        if (edge is GroundEdge straight)
        {
            if ((straight.Nodes[0] == node) && straight.IntermediatePoints.Count > 0)
            {
                (double Lat, double Lon) pt = straight.IntermediatePoints[0];
                return GeoMath.BearingTo(node.Position, new LatLon(pt.Lat, pt.Lon));
            }

            if ((straight.Nodes[1] == node) && straight.IntermediatePoints.Count > 0)
            {
                (double Lat, double Lon) pt = straight.IntermediatePoints[^1];
                return GeoMath.BearingTo(node.Position, new LatLon(pt.Lat, pt.Lon));
            }
        }

        GroundNode otherNode = edge.OtherNode(node);
        return GeoMath.BearingTo(node.Position, otherNode.Position);
    }
}

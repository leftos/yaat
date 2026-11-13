using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

internal static class NavigationCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("NavigationCommandHandler");

    /// <summary>Renders a fix for command responses / SHOWAT descriptions as "NAME (ID)" when it has
    /// an authored display name (e.g. "LAKE CHABOT (VPCBT)"), or the bare identifier otherwise.</summary>
    private static string FixDisplay(string fix) => PhraseologyVerbalizer.FixDisplayTextUpper(fix);

    /// <summary>
    /// Converts a controller-issued magnetic radial from <paramref name="fixName"/> to the true bearing from the fix, with the
    /// station's variation of record (<see cref="RadialConversion.ToTrue"/>).
    /// </summary>
    internal static TrueHeading RadialToTrueBearing(string fixName, double fixLat, double fixLon, int magneticRadial) =>
        RadialConversion.ToTrue(NavigationDatabase.Instance, fixName, new LatLon(fixLat, fixLon), magneticRadial);

    /// <summary>
    /// <see cref="RadialToTrueBearing"/> rounded to whole degrees, which is what a <see cref="BlockTrigger.TrueRadial"/> holds.
    /// </summary>
    internal static int RadialToTrue(string fixName, double fixLat, double fixLon, int magneticRadial) =>
        ToWholeTrueRadial(RadialToTrueBearing(fixName, fixLat, fixLon, magneticRadial));

    /// <summary>A true bearing rounded to whole degrees in 0-359, the form a <see cref="BlockTrigger.TrueRadial"/> holds.</summary>
    internal static int ToWholeTrueRadial(TrueHeading bearing) => (int)Math.Round(bearing.Degrees) % 360;

    internal static CommandResult DispatchJrado(JoinRadialOutboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on radial intercept, fly outbound heading (the radial is magnetic, referenced to the station's variation)
        aircraft.Queue.Blocks.Add(BuildJradoInterceptBlock(cmd));

        return CommandDispatcher.Ok($"Fly present heading, intercept {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial outbound");
    }

    /// <summary>
    /// The block a JRADO leaves queued behind itself: on intercepting the radial, fly it outbound. Built here
    /// rather than inline so <c>CommandDispatcher.RehydrateRestoredBlock</c> rebuilds a restored block from the
    /// same closure; applying the re-parsed instruction instead would set the immediate half and queue a
    /// second block.
    /// </summary>
    internal static CommandBlock BuildJradoInterceptBlock(JoinRadialOutboundCommand cmd)
    {
        TrueHeading trueRadial = RadialToTrueBearing(cmd.FixName, cmd.FixLat, cmd.FixLon, cmd.Radial);
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
                TrueRadial = RadialToTrue(cmd.FixName, cmd.FixLat, cmd.FixLon, cmd.Radial),
                IssuedRadial = cmd.Radial,
            },
            ApplyAction = (ac, _) =>
            {
                var magneticHdg = new MagneticHeading(cmd.Radial);
                ClearRouteApplyingSpeedReverts(ac);
                ac.Targets.TargetTrueHeading = trueRadial;
                ac.Targets.AssignedMagneticHeading = magneticHdg;
                ac.Targets.PreferredTurnDirection = null;
                return new CommandResult(true);
            },
            Description = $"at {FixDisplay(cmd.FixName)} R{cmd.Radial:D3}: FH {cmd.Radial:D3}",
            NaturalDescription = $"On {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial: fly heading {cmd.Radial:D3}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        return block;
    }

    internal static CommandResult DispatchJradi(JoinRadialInboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on radial intercept, navigate inbound to fix
        aircraft.Queue.Blocks.Add(BuildJradiInterceptBlock(cmd));

        return CommandDispatcher.Ok($"Fly present heading, intercept {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial inbound");
    }

    /// <summary>
    /// The block a JRADI leaves queued behind itself: on intercepting the radial, proceed inbound to the fix.
    /// Built here for the same reason as <see cref="BuildJradoInterceptBlock"/>.
    /// </summary>
    internal static CommandBlock BuildJradiInterceptBlock(JoinRadialInboundCommand cmd)
    {
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
                TrueRadial = RadialToTrue(cmd.FixName, cmd.FixLat, cmd.FixLon, cmd.Radial),
                IssuedRadial = cmd.Radial,
            },
            ApplyAction = (ac, _) =>
            {
                ac.Targets.AssignedMagneticHeading = null;
                List<NavigationTarget> holders = TakePendingSpeedReverts(ac.Targets.NavigationRoute);
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = cmd.FixName, Position = new LatLon(cmd.FixLat, cmd.FixLon) });
                ApplyTakenSpeedReverts(ac, holders);
                return new CommandResult(true);
            },
            Description = $"at {FixDisplay(cmd.FixName)} R{cmd.Radial:D3}: DCT {FixDisplay(cmd.FixName)}",
            NaturalDescription = $"On {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial: proceed inbound to {FixDisplay(cmd.FixName)}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Navigation });
        return block;
    }

    internal static CommandResult DispatchDepartFix(DepartFixCommand cmd, AircraftState aircraft)
    {
        // Preserve any crossing restriction already assigned to the depart fix (e.g. a
        // preceding `CFIX CASST 6000 210`). "Depart FIX heading" proceeds direct to the fix
        // and then turns — it must not discard a cross-at altitude/speed the controller set
        // on that same fix, otherwise the aircraft sails over it at default cruise (issue #184).
        NavigationTarget? existing = aircraft.Targets.NavigationRoute.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));

        // Block 0 (immediate): navigate to fix (navigation phase — clear assigned heading)
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = cmd.FixName,
                Position = new LatLon(cmd.FixLat, cmd.FixLon),
                AltitudeRestriction = existing?.AltitudeRestriction,
                SpeedRestriction = existing?.SpeedRestriction,
                RevertAltitude = existing?.RevertAltitude,
                RevertAssignedAltitude = existing?.RevertAssignedAltitude,
                RevertSpeed = existing?.RevertSpeed,
                RevertAssignedSpeed = existing?.RevertAssignedSpeed,
                RevertSpeedFloor = existing?.RevertSpeedFloor,
                RevertSpeedCeiling = existing?.RevertSpeedCeiling,
                RevertSpeedMax = existing?.RevertSpeedMax ?? false,
                RevertResume = existing?.RevertResume,
                RevertMach = existing?.RevertMach,
                RevertMachExplicit = existing?.RevertMachExplicit,
                CrossingTransmissionId = existing?.CrossingTransmissionId,
                SpeedTransmissionId = existing?.SpeedTransmissionId,
            }
        );

        // Block 1: on reaching fix, fly heading (controller heading)
        aircraft.Queue.Blocks.Add(BuildDepartBlock(cmd));

        return CommandDispatcher.Ok($"Proceed direct {FixDisplay(cmd.FixName)}, depart heading {cmd.MagneticHeading.ToDisplayInt():000}");
    }

    /// <summary>
    /// The block a DEPART leaves queued behind itself: on reaching the fix, turn to the depart heading and
    /// publish any crossing-restriction speed stamped on that fix as the aircraft's ceiling. Built here for
    /// the same reason as <see cref="BuildJradoInterceptBlock"/>.
    /// </summary>
    internal static CommandBlock BuildDepartBlock(DepartFixCommand cmd)
    {
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
            },
            ApplyAction = (ac, _) =>
            {
                // A crossing-restriction speed assigned at this fix is an ATC-assigned speed,
                // not a published one — it persists through the depart vector until an approach
                // or via clearance (7110.65 §5-7-1.e assigns it with the vector; §5-7-1.d — only
                // an approach or climb via/descend via clearance cancels it). Publish it as a
                // ceiling so the aircraft does not accelerate back to default cruise after the turn.
                NavigationTarget? departFix = ac.Targets.NavigationRoute.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));
                if (departFix?.SpeedRestriction is { } crossingSpeed && !ac.Targets.HasExplicitSpeedCommand)
                {
                    ac.Targets.SpeedCeiling = crossingSpeed.SpeedKts;
                }

                ac.Targets.NavigationRoute.Clear();
                ac.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(ac.Declination);
                ac.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
                ac.Targets.PreferredTurnDirection = null;
                return new CommandResult(true);
            },
            Description = $"at {FixDisplay(cmd.FixName)}: FH {cmd.MagneticHeading.ToDisplayInt():000}",
            NaturalDescription = $"At {FixDisplay(cmd.FixName)}: fly heading {cmd.MagneticHeading.ToDisplayInt():000}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        return block;
    }

    /// <summary>
    /// The speed state a CFIX speed replaces, captured when the CFIX is issued and restored past its fix: SMAX's re-arm
    /// when SMAX is in force or waits to be armed again (<see cref="SpeedMaxPending"/>), else the Mach hold, else the
    /// plain target speed with its assignment and its bounds. <see cref="Plain"/> says the last applies.
    /// </summary>
    internal readonly record struct SpeedRevertCapture(
        bool SpeedMax,
        double? Mach,
        bool? MachExplicit,
        bool Plain,
        double? Speed,
        double? AssignedSpeed,
        double? Floor,
        double? Ceiling
    )
    {
        internal void WriteTo(NavigationTarget fix)
        {
            fix.RevertSpeed = Speed;
            fix.RevertAssignedSpeed = AssignedSpeed;
            fix.RevertSpeedFloor = Floor;
            fix.RevertSpeedCeiling = Ceiling;
            fix.RevertMach = Mach;
            fix.RevertMachExplicit = MachExplicit;
            fix.RevertSpeedMax = SpeedMax;
            fix.RevertResume = null;
        }
    }

    /// <summary>
    /// What a crossing speed issued now replaces (<see cref="SpeedRevertCapture"/>); nothing when it reverts no speed. A
    /// speed restated while SMAX is in force, or while it waits to be armed again past another crossing, reverts to SMAX
    /// past the last crossing (<see cref="PlaceSpeedMaxRevert"/>), not to a figure it flew. A Mach hold the speed replaces
    /// is restored past the fix in place of a plain speed, and one nobody assigned (live traffic) restores with no
    /// assigned speed, as it had none before.
    /// </summary>
    private static SpeedRevertCapture CaptureSpeedRevert(AircraftState aircraft, bool revertsSpeed)
    {
        ControlTargets targets = aircraft.Targets;
        List<NavigationTarget> route = EffectiveRoute(aircraft);
        bool speedMax = revertsSpeed && SpeedMaxPending(aircraft, route);
        // A Mach still held after an earlier crossing captured it is that crossing's pending revert, not a second one: it
        // moves past this crossing with the pending revert (PlaceCrossingSpeedReverts).
        bool machRevertPending = route.Exists(fix => fix.RevertMach is not null);
        double? mach = (revertsSpeed && !speedMax && !machRevertPending) ? targets.TargetMach : null;
        if (revertsSpeed && !speedMax && (mach is null))
        {
            return new SpeedRevertCapture(
                SpeedMax: false,
                Mach: null,
                MachExplicit: null,
                Plain: true,
                Speed: targets.TargetSpeed,
                AssignedSpeed: targets.AssignedSpeed,
                Floor: targets.SpeedFloor,
                Ceiling: targets.SpeedCeiling
            );
        }

        bool? machExplicit = (mach is not null) ? targets.HasExplicitSpeedCommand : null;
        return new SpeedRevertCapture(
            SpeedMax: speedMax,
            Mach: mach,
            MachExplicit: machExplicit,
            Plain: false,
            Speed: null,
            AssignedSpeed: null,
            Floor: null,
            Ceiling: null
        );
    }

    /// <summary>
    /// The speed reverts already on a route before a CFIX, or a constrained direct-to that keeps the active STAR, stamps
    /// its fixes: the Mach revert, the plain one and a resume.
    /// </summary>
    internal readonly record struct PendingSpeedReverts(
        double? Mach,
        bool? MachExplicit,
        NavigationTarget? PlainHolder,
        double? Speed,
        double? AssignedSpeed,
        double? Floor,
        double? Ceiling,
        SpeedResumeKind? Resume
    )
    {
        internal static PendingSpeedReverts Read(List<NavigationTarget> route)
        {
            NavigationTarget? machHolder = route.Find(fix => fix.RevertMach is not null);
            NavigationTarget? plainHolder = route.Find(HasPlainRevert);
            return new PendingSpeedReverts(
                machHolder?.RevertMach,
                machHolder?.RevertMachExplicit,
                plainHolder,
                plainHolder?.RevertSpeed,
                plainHolder?.RevertAssignedSpeed,
                plainHolder?.RevertSpeedFloor,
                plainHolder?.RevertSpeedCeiling,
                route.Find(fix => fix.RevertResume is not null)?.RevertResume
            );
        }
    }

    /// <summary>Whether <paramref name="fix"/> holds a plain speed revert: the speed, its assignment or a bound.</summary>
    internal static bool HasPlainRevert(NavigationTarget fix) =>
        (fix.RevertSpeed is not null)
        || (fix.RevertAssignedSpeed is not null)
        || (fix.RevertSpeedFloor is not null)
        || (fix.RevertSpeedCeiling is not null);

    /// <summary>
    /// After a CFIX, or a constrained direct-to that keeps the active STAR, stamps a crossing speed, leaves one speed
    /// revert on the route, on the route-order-last fix whose speed the controller set, so every crossing speed before it
    /// is flown first: SMAX's re-arm (<see cref="PlaceSpeedMaxRevert"/>); else the Mach revert already pending
    /// (<see cref="PlaceMachRevert"/>); else, for a plain capture, the resume or the plain revert already pending, from the
    /// one capture that made it, or the one captured now unless this CFIX restates a crossing speed
    /// (<paramref name="speedRestated"/>) and so captures none (the constrained direct-to never restates).
    /// </summary>
    internal static void PlaceCrossingSpeedReverts(
        List<NavigationTarget> route,
        SpeedRevertCapture capture,
        PendingSpeedReverts pending,
        bool speedRestated
    )
    {
        if (capture.SpeedMax)
        {
            PlaceSpeedMaxRevert(route);
            return;
        }

        if (capture.Mach is not null)
        {
            return;
        }

        if (pending.Mach is { } mach)
        {
            PlaceMachRevert(route, mach, pending.MachExplicit);
            return;
        }

        if (!capture.Plain)
        {
            return;
        }

        if (pending.Resume is { } resume)
        {
            PlaceResumeRevert(route, resume);
        }
        else if (pending.PlainHolder is not null)
        {
            PlacePlainRevert(route, pending.Speed, pending.AssignedSpeed, pending.Floor, pending.Ceiling);
        }
        else if (!speedRestated)
        {
            PlacePlainRevert(route, capture.Speed, capture.AssignedSpeed, capture.Floor, capture.Ceiling);
        }
    }

    /// <summary>
    /// Stamps a crossing restriction on <paramref name="cmd"/>'s fix, with the altitude and speed to revert to once the
    /// fix is passed. After a DVIA or CVIA of this same transmission has applied, the crossing is restated with the via
    /// clearance: it takes no reverts and leaves alone the assigned altitude that clearance cleared. Before the via
    /// clearance applies the CFIX takes its reverts like any other, and the via clearance drops them if and when it applies
    /// (<see cref="SupersedeEarlierCrossingRestrictions"/>), so a via clearance dropped or refused first leaves them.
    /// An altitude revert that would reverse the direction of flight is not captured either: the aircraft keeps the crossing
    /// altitude past the fix (<see cref="AltitudeRevertReversesTravel"/>). Every CFIX takes the transmission's crossing stamp;
    /// only one carrying a speed takes its speed stamp, so an altitude-only CFIX leaves the fix's speed, and the
    /// transmission that set it, as they were. A crossing speed the type can fly neither at its present altitude nor at the
    /// crossing altitude is refused before anything changes, as SPD is (<see cref="FlightCommandHandler.RefuseUnflyableSpeed"/>),
    /// and so is any crossing speed for an arrival on final inside 5 nm (7110.65 §5-7-1.b.4).
    /// </summary>
    internal static CommandResult DispatchCrossFix(CrossFixCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        // While a SID handover is pending the crossing acts on the route the phase will hand over, never on the live route,
        // which stays empty so the coded legs keep steering.
        List<NavigationTarget>? pendingRoute = PendingDepartureRoute(aircraft);
        if (RefuseCrossFix(cmd, aircraft, ctx, pendingRoute) is { } refused)
        {
            return refused;
        }

        bool handoverPending = pendingRoute is not null;
        List<NavigationTarget> route = pendingRoute ?? aircraft.Targets.NavigationRoute;
        int transmissionId = ctx.TransmissionId;

        // The altitude and speed to revert to after fix passage.
        bool restatedWithVia = ViaAppliedInTransmission(aircraft, transmissionId);
        CifpAltitudeRestriction altRestriction = FlightCommandHandler.ToAltitudeRestriction(cmd.Altitude, cmd.AltType);
        bool revertsAltitude = (!restatedWithVia) && CapturesAltitudeRevert(aircraft, altRestriction);
        double? previousAlt = revertsAltitude ? aircraft.Targets.TargetAltitude : null;
        double? previousAssignedAlt = revertsAltitude ? aircraft.Targets.AssignedAltitude : null;
        SpeedRevertCapture capture = CaptureSpeedRevert(aircraft, revertsSpeed: (cmd.Speed is not null) && !restatedWithVia);

        CifpSpeedRestriction? speedRestriction = cmd.Speed is { } spd ? new CifpSpeedRestriction(spd, CifpSpeedRestrictionType.AtOrBelow) : null;

        // Id 0 is no transmission (a queued block restored from a snapshot written before blocks carried one).
        int? crossingTransmissionId = StampedTransmission(transmissionId);

        // The reverts already on the route are read before the stamp below, which clears them when this CFIX restates the
        // speed on the fix that holds them.
        var pending = PendingSpeedReverts.Read(route);

        // CFIX stamps a fix already on the route in place, keeping the other fixes and their restrictions, so CFIX commands
        // are additive. A fix not on the route is appended, so a chain of CFIX on a vectored aircraft builds a crossing
        // profile in issue order (an empty route becomes a single-fix direct route). A coded-leg fix a pending route lacks
        // goes first, where the handover drops it as flown and applies its reverts.
        // The crossing fix replaces the clearance-limit direct: it is never appended after the airport, nor stamped on it.
        if (!handoverPending)
        {
            aircraft.Targets.StripClearanceLimitDirect();
        }

        NavigationTarget? existing = route.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));
        // A crossing speed restated on a fix whose speed a controller already set, in any transmission, changes the
        // crossing figure only: what the aircraft goes back to past the fix is still what the first crossing replaced.
        // A fix whose crossing came outside a transmission carries no stamp but still holds its reverts, so it counts as a
        // controller crossing too. A later speed instruction in between has already cleared both (CancelEarlierCrossingSpeeds).
        bool speedRestated = (speedRestriction is not null) && (existing is not null) && FlightPhysics.IsControllerSetSpeed(existing);
        NavigationTarget fix = existing ?? new NavigationTarget { Name = cmd.FixName, Position = new LatLon(cmd.FixLat, cmd.FixLon) };
        if (existing is null)
        {
            route.Insert(handoverPending ? 0 : route.Count, fix);
        }

        fix.AltitudeRestriction = altRestriction;
        fix.RevertAltitude = previousAlt;
        fix.RevertAssignedAltitude = previousAssignedAlt;
        fix.CrossingTransmissionId = crossingTransmissionId;
        if (speedRestriction is not null)
        {
            fix.SpeedRestriction = speedRestriction;
            fix.SpeedTransmissionId = crossingTransmissionId;
            if (!speedRestated)
            {
                capture.WriteTo(fix);
            }

            PlaceCrossingSpeedReverts(route, capture, pending, speedRestated);
        }

        // Let the planner handle altitude on the next tick — don't set TargetAltitude directly.
        // But do set AssignedAltitude for the datablock display, unless this transmission's DVIA or CVIA already applied
        // and cleared it: both orders of "CFIX, DVIA" and of "CFIX, CVIA" then end with no assigned altitude. A cleared
        // altitude that already meets the crossing's bound stays the altitude flown (CrossingBoundMetByClearance). While a
        // SID handover is pending only a floor above the cleared altitude is written (AssignsCrossingAltitude): any other crossing
        // there gives no new altitude to maintain, and the handover reads the assigned altitude to choose the altitude it
        // holds after the coded legs.
        if ((!restatedWithVia) && AssignsCrossingAltitude(aircraft, altRestriction, handoverPending))
        {
            aircraft.Targets.AssignedAltitude = cmd.Altitude;
        }

        if (cmd.Speed is { } spdVal)
        {
            aircraft.Targets.AssignedSpeed = spdVal;
            aircraft.Targets.SpeedLimitsLifted = false;
            // A crossing speed is an ATC speed act like SPD, so it ends a forced speed's override of the final gate.
            aircraft.Targets.SpeedOverridesFinalGate = false;
            // A Mach hold stays live until the planner hands it over to the crossing speed (PlanCrossingUnderMach).
            FlightCommandHandler.EndSpeedMaxHold(aircraft);

            // An acceleration toward the CFIX speed starts at once; the look-ahead planner (UpdateSpeedPlanning) times a
            // deceleration.
            if (spdVal > aircraft.IndicatedAirspeed)
            {
                aircraft.Targets.TargetSpeed = spdVal;
            }

            // Restated with a via clearance that has already applied: apply the first constrained fix again, as that
            // clearance does, so "DVIA, CFIX X 110 230" ends where "CFIX X 110 230, DVIA" does.
            if (restatedWithVia)
            {
                ApplyFirstConstrainedFix(aircraft);
            }
        }

        return CommandDispatcher.Ok(CrossFixReadback(cmd));
    }

    /// <summary>
    /// The route a crossing restriction acts on: while a SID handover is pending (<see cref="IPendingDepartureRoute"/>), the
    /// pending route the departure phase will hand over, the one home of a crossing given there, which the handover re-adds
    /// as it is; otherwise the live route.
    /// </summary>
    internal static List<NavigationTarget> EffectiveRoute(AircraftState aircraft) =>
        PendingDepartureRoute(aircraft) ?? aircraft.Targets.NavigationRoute;

    /// <summary>
    /// The pending route of the current departure phase while a SID handover will load it
    /// (<see cref="IPendingDepartureRoute.PendingRoute"/>); null when no handover is pending.
    /// </summary>
    internal static List<NavigationTarget>? PendingDepartureRoute(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is IPendingDepartureRoute { PendingRoute: { } pending } ? pending : null;

    /// <summary>
    /// Refuses a CFIX before anything changes: a crossing speed for an arrival on final inside 5 nm, as a plain SPD is
    /// (7110.65 §5-7-1.b.4; a crossing with no speed is an altitude instruction and is accepted), a crossing speed the type
    /// cannot fly, and, while a SID handover is pending, a fix off the pending route or a coded-leg fix already behind the
    /// aircraft (<see cref="RefuseOffPendingRoute"/>). Null when the crossing may go ahead.
    /// </summary>
    private static CommandResult? RefuseCrossFix(
        CrossFixCommand cmd,
        AircraftState aircraft,
        DispatchContext ctx,
        List<NavigationTarget>? pendingRoute
    )
    {
        if ((cmd.Speed is not null) && (FlightCommandHandler.RefuseInsideFinalGate(aircraft) is { } insideFinal))
        {
            return insideFinal;
        }

        if (
            (cmd.Speed is { } crossingSpeed)
            && (FlightCommandHandler.RefuseUnflyableSpeed(aircraft, ctx, crossingSpeed, SpeedModifier.None, cmd.Altitude) is { } unflyable)
        )
        {
            return unflyable;
        }

        return RefuseOffPendingRoute(aircraft, pendingRoute, cmd.FixName);
    }

    /// <summary>
    /// While a SID handover is pending (<paramref name="pendingRoute"/> not null), refuses as unable a crossing at a fix
    /// that is neither on the pending route nor one of the coded legs' fixes the current phase flies or hands to. A filed
    /// route token the pending route lacks is refused too: it is behind the aircraft, stripped as colocated with the
    /// departure, or no fix ahead. So is a coded-leg fix whose leg has already sequenced
    /// (<see cref="RefusePassedCodedFix"/>). Null when the crossing may go ahead.
    /// </summary>
    private static CommandResult? RefuseOffPendingRoute(AircraftState aircraft, List<NavigationTarget>? pendingRoute, string fixName)
    {
        if (pendingRoute is null)
        {
            return null;
        }

        if (RefusePassedCodedFix(aircraft, fixName) is { } passed)
        {
            return passed;
        }

        if (pendingRoute.Exists(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        IReadOnlyList<ProcedureLeg> codedLegs = aircraft.Phases?.CurrentPhase switch
        {
            DepartureProcedurePhase procedure => procedure.Legs,
            InitialClimbPhase climb => climb.DepartureProcedureLegs ?? [],
            _ => [],
        };
        if (codedLegs.Any(l => string.Equals(l.FixName, fixName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new CommandResult(false, $"unable, {FixDisplay(fixName)} isn't on our route");
    }

    /// <summary>
    /// While the departure phase flies its coded legs, refuses as unable a crossing at a coded-leg fix whose leg has already
    /// sequenced and that no leg still ahead ends at, nor any pending-route fix beyond the coded legs: the fix is behind the
    /// aircraft. Null when the crossing may go ahead, a fix on the active leg still ahead included.
    /// </summary>
    private static CommandResult? RefusePassedCodedFix(AircraftState aircraft, string fixName)
    {
        if (aircraft.Phases?.CurrentPhase is not DepartureProcedurePhase procedure)
        {
            return null;
        }

        bool passed = procedure.Legs.Take(procedure.ActiveLegIndex).Any(l => NamesFix(l.FixName, fixName)) && !FixAhead(procedure, fixName);
        return passed ? new CommandResult(false, $"unable, we're already past {FixDisplay(fixName)}") : null;
    }

    /// <summary>
    /// Whether <paramref name="fixName"/> still lies ahead of the departure phase: a coded leg from the active one on ends
    /// there, or the pending route names it again beyond the coded legs.
    /// </summary>
    private static bool FixAhead(DepartureProcedurePhase procedure, string fixName)
    {
        if (procedure.Legs.Skip(procedure.ActiveLegIndex).Any(l => NamesFix(l.FixName, fixName)))
        {
            return true;
        }

        var coded = new HashSet<string>(procedure.Legs.Where(l => l.FixName is not null).Select(l => l.FixName!), StringComparer.OrdinalIgnoreCase);
        return procedure.PostRoute.SkipWhile(f => coded.Contains(f.Name)).Any(f => NamesFix(f.Name, fixName));
    }

    private static bool NamesFix(string? name, string fixName) => string.Equals(name, fixName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a CFIX captures the altitude to revert to past its fix: not when the crossing reverses the direction of
    /// flight (<see cref="AltitudeRevertReversesTravel"/>), nor in a SID's climb window when the clearance in force already
    /// meets its bound (<see cref="WindowCrossingMet"/>), which changes nothing.
    /// </summary>
    private static bool CapturesAltitudeRevert(AircraftState aircraft, CifpAltitudeRestriction restriction)
    {
        if (WindowCrossingMet(aircraft, restriction))
        {
            return false;
        }

        return !AltitudeRevertReversesTravel(aircraft.Altitude, restriction.Altitude1Ft, aircraft.Targets.TargetAltitude);
    }

    /// <summary>
    /// Whether, while the departure phase flies its coded legs, the clearance in force already meets a crossing's bound:
    /// under a climb via, an at-or-above bound at or below the via's top; otherwise the cleared altitude, or the SID hold
    /// altitude with none assigned (<see cref="WindowHoldOrAssigned"/>), lying beyond it by the rules of
    /// <see cref="CrossingBoundMetByClearance"/>.
    /// </summary>
    private static bool WindowCrossingMet(AircraftState aircraft, CifpAltitudeRestriction restriction)
    {
        if (aircraft.Phases?.CurrentPhase is not DepartureProcedurePhase { PendingRoute: not null })
        {
            return false;
        }

        if (aircraft.Procedure.ClimbViaTargetFt is { } top)
        {
            return (restriction.Type == CifpAltitudeRestrictionType.AtOrAbove) && (restriction.Altitude1Ft <= top);
        }

        return BoundMetByCleared(aircraft, restriction, WindowHoldOrAssigned(aircraft));
    }

    /// <summary>
    /// Whether a CFIX writes its altitude as the assigned altitude. Off the climb window: unless the cleared altitude already
    /// meets its bound. While a SID handover is pending: only an "at" or at-or-above floor above the cleared altitude (or the
    /// SID hold altitude with none assigned) while the departure phase flies its coded legs and no via is in force, so the
    /// aircraft climbs to the floor once the earlier legs' caps release and stays there past the fix; under a via the top
    /// caps the crossing, and any other crossing there is a temporary deviation that assigns nothing.
    /// </summary>
    private static bool AssignsCrossingAltitude(AircraftState aircraft, CifpAltitudeRestriction restriction, bool handoverPending)
    {
        if (!handoverPending)
        {
            return !CrossingBoundMetByClearance(aircraft, restriction);
        }

        bool floor = restriction.Type is CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.AtOrAbove;
        if ((!floor) || AltitudeRestrictionInForce.ViaClearanceInForce(aircraft) || (WindowHoldOrAssigned(aircraft) is not { } cleared))
        {
            return false;
        }

        return restriction.Altitude1Ft > cleared;
    }

    /// <summary>
    /// The cleared altitude while the departure phase flies its coded legs: the assigned altitude, else the SID hold altitude
    /// the phase flies. Null outside the departure phase or with neither.
    /// </summary>
    private static double? WindowHoldOrAssigned(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is DepartureProcedurePhase { PendingRoute: not null } procedure
            ? aircraft.Targets.AssignedAltitude ?? procedure.SidHoldAltitude(aircraft)
            : null;

    /// <summary>The readback of a CFIX: the fix, the altitude with its at / at or above / at or below, and any speed.</summary>
    private static string CrossFixReadback(CrossFixCommand cmd)
    {
        string altTypeStr = cmd.AltType switch
        {
            CrossFixAltitudeType.AtOrAbove => "at or above",
            CrossFixAltitudeType.AtOrBelow => "at or below",
            _ => "at",
        };
        string cfixMsg = $"Cross {FixDisplay(cmd.FixName)} {altTypeStr} {cmd.Altitude:N0}";
        if (cmd.Speed is not null)
        {
            cfixMsg += $", speed {cmd.Speed}";
        }

        return cfixMsg;
    }

    /// <summary>
    /// Whether reverting to <paramref name="previousTarget"/> once past a fix crossed at <paramref name="crossAltitude"/>
    /// would reverse the direction the aircraft flies to make the crossing. Going back to an altitude already left needs
    /// a new clearance (7110.65 4-5-7.h.8 NOTE 2; 14 CFR 91.123), and the crossing overrides the earlier altitude
    /// (AIM 4-4-10.g); a revert that continues the same way stands (AIM 4-4-10.e). The direction of travel is from
    /// <paramref name="currentAltitude"/> toward the crossing altitude; an aircraft already at the crossing altitude (within
    /// the altitude snap) has no direction of travel and keeps its revert.
    /// </summary>
    internal static bool AltitudeRevertReversesTravel(double currentAltitude, double crossAltitude, double? previousTarget)
    {
        if (previousTarget is not { } revert)
        {
            return false;
        }

        if (Math.Abs(crossAltitude - currentAltitude) <= FlightPhysics.AltitudeSnapFt)
        {
            return false;
        }

        int travel = Math.Sign(crossAltitude - currentAltitude);
        int revertDirection = Math.Sign(revert - crossAltitude);
        return (travel != 0) && (revertDirection != 0) && (travel != revertDirection);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/>'s cleared altitude (<see cref="ClearedAltitude"/>) already lies
    /// beyond <paramref name="restriction"/>'s bound: an at-or-above bound below it, an at-or-below bound above it, or a
    /// window it lies strictly inside. Such a bound does not move the aircraft: it keeps flying to the cleared altitude,
    /// which stays the altitude flown (AIM 4-4-10.a; 7110.65 4-5-7). A cleared altitude equal to the bound (as a CFIX that
    /// assigned its own bound leaves it) is not met, and the planner plans the crossing itself. A plain "at" crossing is never met
    /// this way, nor is any bound with no cleared altitude, or under a descend or climb via, which flies to the depicted
    /// altitudes. Nor is a published approach floor in force below the cleared altitude (an at-or-above bound, or a window's
    /// lower bound with the cleared altitude above the window): once established on a segment of a cleared approach the
    /// aircraft descends to the approach's altitudes (AIM 5-4-7.f; 7110.65 4-8-1.a.4), while an approach ceiling above the
    /// cleared altitude, or a window it lies strictly inside, still leaves it at the cleared altitude rather than climbing.
    /// </summary>
    internal static bool CrossingBoundMetByClearance(AircraftState aircraft, CifpAltitudeRestriction restriction) =>
        BoundMetByCleared(aircraft, restriction, ClearedAltitude(aircraft));

    /// <summary>
    /// The altitude a crossing is judged against: the assigned altitude, else the TDLS "maintain" altitude a bare departure
    /// clearance stored (<see cref="AircraftProcedure.SidInitialAltitudeFt"/>). The maintain counts only on an IFR flight,
    /// outside a descend or climb via and with no active or pending approach clearance, which supersede it; null with none.
    /// </summary>
    internal static double? ClearedAltitude(AircraftState aircraft) =>
        aircraft.Targets.AssignedAltitude
        ?? (
            (!aircraft.FlightPlan.IsVfr)
            && (!AltitudeRestrictionInForce.ViaClearanceInForce(aircraft))
            && (aircraft.Phases?.ActiveApproach is null)
            && (aircraft.Approach.PendingClearance is null)
            && (aircraft.Procedure.SidInitialAltitudeFt is { } maintain)
                ? (double?)maintain
                : null
        );

    /// <summary>
    /// Whether <paramref name="cleared"/> already lies beyond <paramref name="restriction"/>'s bound, by the rules of
    /// <see cref="CrossingBoundMetByClearance"/>; false with no cleared altitude or under a via clearance.
    /// </summary>
    private static bool BoundMetByCleared(AircraftState aircraft, CifpAltitudeRestriction restriction, double? cleared)
    {
        if (AltitudeRestrictionInForce.ViaClearanceInForce(aircraft) || (cleared is not { } altitude))
        {
            return false;
        }

        // A window with the cleared altitude above it is already not met by the switch below; only the at-or-above floor
        // needs the approach exception.
        if (
            (restriction.Source == CifpAltitudeRestrictionSource.Approach)
            && (restriction.Type == CifpAltitudeRestrictionType.AtOrAbove)
            && AltitudeRestrictionInForce.IsInForce(aircraft, restriction)
        )
        {
            return false;
        }

        return restriction.Type switch
        {
            CifpAltitudeRestrictionType.AtOrAbove => altitude > restriction.Altitude1Ft,
            CifpAltitudeRestrictionType.AtOrBelow => altitude < restriction.Altitude1Ft,
            CifpAltitudeRestrictionType.Between => (restriction.Altitude2Ft is { } lower)
                && (altitude > lower)
                && (altitude < restriction.Altitude1Ft),
            _ => false,
        };
    }

    /// <summary>
    /// A "maintain" (CM or DM) accepted while a SID handover is pending (<see cref="PendingDepartureRoute"/>: the initial
    /// climb or the departure phase's coded legs) cancels the altitude of every controller crossing an earlier transmission
    /// stamped on the pending route (<see cref="CrossingStampedBefore"/>): the fix gets the SID's published restriction
    /// back, as a via clearance gives it (<see cref="SupersedeEarlierCrossingRestrictions"/>), and loses its altitude
    /// reverts. A crossing stamped in the maintain's own transmission stays (7110.65 4-2-5.b Example 1), and every crossing
    /// keeps its speed and speed reverts, since speed restrictions remain in effect until the controller cancels them
    /// (7110.65 4-5-7.h.11 NOTE 1). The live route is left alone. Returns <paramref name="result"/> unchanged.
    /// </summary>
    internal static CommandResult CancelEarlierWindowCrossingAltitudes(CommandResult result, AircraftState aircraft, int transmissionId)
    {
        if ((!result.Success) || (PendingDepartureRoute(aircraft) is not { } pending))
        {
            return result;
        }

        var published = new Lazy<Dictionary<string, NavigationTarget>>(() => ActiveSidRestrictions(aircraft));
        foreach (
            NavigationTarget fix in pending.Where(f =>
                (f.AltitudeRestriction?.Source == CifpAltitudeRestrictionSource.Atc) && CrossingStampedBefore(f, transmissionId)
            )
        )
        {
            fix.AltitudeRestriction = published.Value.GetValueOrDefault(fix.Name)?.AltitudeRestriction;
            fix.CrossingTransmissionId = null;
            fix.RevertAltitude = null;
            fix.RevertAssignedAltitude = null;
            Log.LogDebug("{Callsign}: the maintain cancels the crossing altitude at {Fix} on the pending route", aircraft.Callsign, fix.Name);
        }

        return result;
    }

    /// <summary>
    /// Whether <paramref name="fix"/>'s crossing was stamped in a transmission before <paramref name="transmissionId"/>: always
    /// for a transmission with no id (one carrying no crossing, which follows every stamped one) or a crossing with no
    /// stamp; otherwise only for a smaller id, so a queued maintain that fires after a later crossing leaves that crossing.
    /// </summary>
    private static bool CrossingStampedBefore(NavigationTarget fix, int transmissionId) =>
        (transmissionId == 0) || (fix.CrossingTransmissionId is not { } stamp) || (stamp < transmissionId);

    internal static CommandResult DispatchListApproaches(ListApproachesCommand cmd, AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string airport;
        if (cmd.AirportCode is not null)
        {
            if (!navDb.TryResolveAirport(cmd.AirportCode, out string? canonical))
            {
                return new CommandResult(false, $"Unknown airport {cmd.AirportCode.Trim().ToUpperInvariant()}");
            }
            airport = canonical;
        }
        else
        {
            airport = aircraft.FlightPlan.Destination;
        }

        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "No airport specified and no destination in flight plan");
        }

        IReadOnlyList<CifpApproachProcedure> approaches = navDb.GetApproaches(airport);
        if (approaches.Count == 0)
        {
            return CommandDispatcher.Ok($"No approaches found for {airport.ToUpperInvariant()}");
        }

        IOrderedEnumerable<IGrouping<string, CifpApproachProcedure>> grouped = approaches.GroupBy(a => a.Runway ?? "").OrderBy(g => g.Key);

        IEnumerable<string> parts = grouped.Select(g =>
        {
            string items = string.Join(", ", g.Select(a => FormatApproachDisplay(a)));
            return g.Key.Length > 0 ? $"RWY {RunwayIdentifier.ToDisplayDesignator(g.Key)}: {items}" : items;
        });

        return CommandDispatcher.Ok($"{airport.ToUpperInvariant()} approaches: {string.Join(" | ", parts)}");
    }

    private static string FormatApproachDisplay(CifpApproachProcedure approach)
    {
        string typeName = approach.ApproachTypeName;
        int parenIdx = typeName.IndexOf('(');
        if (parenIdx >= 0)
        {
            typeName = typeName[..parenIdx];
        }

        string rwy = approach.Runway ?? "";

        // Extract variant: anything in ApproachId after type code + runway
        string variant = "";
        if (approach.ApproachId.Length > 1 + rwy.Length)
        {
            variant = approach.ApproachId[(1 + rwy.Length)..];
        }

        return $"{typeName}{rwy}{variant}";
    }

    internal static CommandResult DispatchJarr(JoinStarCommand cmd, AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;

        // Resolve a controller-typed STAR id that may omit the version digit (JARR TEJAS → TEJAS5).
        if (aircraft.FlightPlan.Destination is { } destination && !string.IsNullOrEmpty(destination))
        {
            cmd = cmd with { StarId = navDb.ResolveCommandStarId(destination, cmd.StarId) };
        }

        // A runway-transition argument (JARR star 27) also designates the landing runway, so the
        // CIFP runway-transition lookup and downstream approach selection use it. Zero-pad it to the
        // same form as RunwayInfo.Designator so a single-digit designator ("1R") matches the CIFP
        // "RW01R" key rather than silently dropping the transition (issue #273).
        if (cmd.RunwayTransition is { } runwayArg)
        {
            aircraft.Procedure.DestinationRunway = RunwayIdentifier.NormalizeDesignator(runwayArg);
        }

        // Try CIFP STAR first for constrained navigation targets
        List<NavigationTarget>? cifpResult = TryResolveStarFromCifp(cmd, aircraft, out ProcedureSource? starSource);
        if (cifpResult is not null)
        {
            List<NavigationTarget> holders = TakePendingSpeedReverts(aircraft.Targets.NavigationRoute);
            aircraft.Targets.NavigationRoute.Clear();
            aircraft.Targets.AssignedMagneticHeading = null;
            foreach (NavigationTarget target in cifpResult)
            {
                aircraft.Targets.NavigationRoute.Add(target);
            }

            aircraft.Procedure.ActiveStarId = cmd.StarId;
            aircraft.Procedure.StarViaMode = false; // STAR via mode OFF by default
            aircraft.Procedure.StarViaTransmissionId = null;
            aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
            // An RPS given with a crossing on the old route resumes this STAR's published speeds, not the old one's.
            ApplyTakenSpeedReverts(aircraft, holders);

            string cifpFixList = string.Join(" ", cifpResult.Select(t => FixDisplay(t.Name)));
            return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {cifpFixList}") with
            {
                Advisory = CommandDispatcher.ProcedureSourceAdvisory("STAR", cmd.StarId, starSource),
            };
        }

        // Fallback to NavData body fixes (lateral path only, no constraints)
        IReadOnlyList<string>? starBody = navDb.GetStarBody(cmd.StarId);
        if (starBody is null || starBody.Count == 0)
        {
            return new CommandResult(false, $"Unknown STAR: {cmd.StarId}");
        }

        List<string> routeFixes;

        if (cmd.Transition is not null)
        {
            IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? transitions = navDb.GetStarTransitions(cmd.StarId);
            (string Name, IReadOnlyList<string> Fixes)? match = transitions?.FirstOrDefault(t =>
                t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null && match.Value.Fixes is not null)
            {
                routeFixes = [.. match.Value.Fixes, .. starBody];
            }
            else
            {
                // No transition matched — try joining at an intermediate fix in the body
                int fixIdx = starBody.ToList().FindIndex(f => f.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
                if (fixIdx >= 0)
                {
                    routeFixes = [.. starBody.Skip(fixIdx)];
                }
                else
                {
                    return new CommandResult(false, $"Unknown transition or fix '{cmd.Transition}' for STAR {cmd.StarId}");
                }
            }
        }
        else
        {
            routeFixes = FindStarFixesAhead(aircraft, starBody);
        }

        if (routeFixes.Count == 0)
        {
            return new CommandResult(false, $"No navigable fixes found for STAR {cmd.StarId}");
        }

        // Deduplicate adjacent identical fix names
        var deduped = new List<string>(routeFixes.Count);
        foreach (string name in routeFixes)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                deduped.Add(name);
            }
        }

        List<NavigationTarget> pendingReverts = TakePendingSpeedReverts(aircraft.Targets.NavigationRoute);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.AssignedMagneticHeading = null;
        foreach (string fixName in deduped)
        {
            (double Lat, double Lon)? pos = navDb.GetFixPosition(fixName);
            if (pos is not null)
            {
                aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fixName, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
            }
        }

        if (aircraft.Targets.NavigationRoute.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes for STAR {cmd.StarId}");
        }

        // Set STAR state even for NavData fallback (allows DVIA later)
        aircraft.Procedure.ActiveStarId = cmd.StarId;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
        ApplyTakenSpeedReverts(aircraft, pendingReverts);

        string fixListStr = string.Join(" ", deduped.Select(FixDisplay));
        return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {fixListStr}");
    }

    /// <summary>
    /// Attempts to resolve a STAR from CIFP data with altitude/speed constraints.
    /// Builds ordered leg sequence: enroute transition → common → runway transition.
    /// Returns null if CIFP data is unavailable or STAR cannot be resolved.
    /// </summary>
    private static List<NavigationTarget>? TryResolveStarFromCifp(JoinStarCommand cmd, AircraftState aircraft, out ProcedureSource? source)
    {
        source = null;
        if (aircraft.FlightPlan.Destination is null)
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        CifpStarProcedure? star = navDb.GetStar(aircraft.FlightPlan.Destination, cmd.StarId, out source);
        if (star is null)
        {
            return null;
        }

        // Build ordered leg sequence: enroute transition → common → runway transition
        var orderedLegs = new List<CifpLeg>();

        // Enroute transition (if specified)
        bool transitionMatched = false;
        if (cmd.Transition is not null && star.EnrouteTransitions.TryGetValue(cmd.Transition, out CifpTransition? enTransition))
        {
            orderedLegs.AddRange(enTransition.Legs);
            transitionMatched = true;
        }

        orderedLegs.AddRange(star.CommonLegs);

        // Runway transition: prefer the live AssignedRunway, fall back to the procedure's
        // DestinationRunway. EAPP sets DestinationRunway without touching AssignedRunway, so
        // a JARR issued after EAPP can still pick up the right transition.
        string? rwDesignator = aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Procedure.DestinationRunway;
        if (!string.IsNullOrEmpty(rwDesignator))
        {
            CifpTransition? rwTransition = LookupRunwayTransition(star.RunwayTransitions, rwDesignator);
            if (rwTransition is not null)
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        // Convert legs to NavigationTargets with constraints
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);

        // If transition was specified but didn't match, try joining at an intermediate fix
        if (cmd.Transition is not null && !transitionMatched)
        {
            int fixIdx = targets.FindIndex(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
            if (fixIdx >= 0)
            {
                return targets.GetRange(fixIdx, targets.Count - fixIdx);
            }

            // Check each enroute transition for the fix
            foreach ((string _, CifpTransition? trans) in star.EnrouteTransitions)
            {
                List<NavigationTarget> transTargets = DepartureClearanceHandler.ResolveLegsToTargets(trans.Legs);
                int transFixIdx = transTargets.FindIndex(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
                if (transFixIdx >= 0)
                {
                    List<NavigationTarget> result = transTargets.GetRange(transFixIdx, transTargets.Count - transFixIdx);
                    result.AddRange(targets);
                    return result;
                }
            }

            // Fix not found in CIFP data — fall through to NavData
            return null;
        }

        // Filter to fixes ahead of aircraft (same logic as NavData fallback)
        if (cmd.Transition is null && targets.Count > 1)
        {
            targets = FindTargetsAhead(aircraft, targets);
        }

        return targets.Count > 0 ? targets : null;
    }

    /// <summary>
    /// Resolves a CIFP STAR/SID runway-transition entry by runway designator. Handles the
    /// "B" wildcard convention (e.g. <c>RW01B</c> covers both L and R parallels).
    /// </summary>
    internal static CifpTransition? LookupRunwayTransition(IReadOnlyDictionary<string, CifpTransition> transitions, string runwayDesignator)
    {
        // CIFP runway-transition keys are zero-padded ("RW01R"); a controller-typed single-digit
        // designator ("1R") must be padded before the lookup or it silently misses the transition.
        string designator = RunwayIdentifier.NormalizeDesignator(runwayDesignator);
        string rwKey = "RW" + designator;
        if (transitions.TryGetValue(rwKey, out CifpTransition? rwTransition))
        {
            return rwTransition;
        }
        string bothKey = "RW" + designator.TrimEnd('L', 'R', 'C') + "B";
        return transitions.TryGetValue(bothKey, out CifpTransition? bothTransition) ? bothTransition : null;
    }

    /// <summary>
    /// Fix names that appear only on runway transitions other than <paramref name="selectedTransition"/>.
    /// </summary>
    internal static HashSet<string> CollectExclusiveRunwayTransitionFixes(CifpStarProcedure star, CifpTransition selectedTransition)
    {
        var selectedFixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CifpLeg leg in selectedTransition.Legs)
        {
            if (!string.IsNullOrEmpty(leg.FixIdentifier))
            {
                selectedFixes.Add(leg.FixIdentifier);
            }
        }

        var exclusive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CifpTransition otherTransition in star.RunwayTransitions.Values)
        {
            if (ReferenceEquals(otherTransition, selectedTransition))
            {
                continue;
            }

            foreach (CifpLeg leg in otherTransition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier) && !selectedFixes.Contains(leg.FixIdentifier))
                {
                    exclusive.Add(leg.FixIdentifier);
                }
            }
        }

        return exclusive;
    }

    /// <summary>
    /// Removes nav-route targets that belong exclusively to non-selected STAR runway transitions.
    /// </summary>
    internal static void RemoveStaleStarRunwayTransitionFixes(AircraftState aircraft, CifpStarProcedure star, CifpTransition selectedTransition)
    {
        HashSet<string> stale = CollectExclusiveRunwayTransitionFixes(star, selectedTransition);
        if (stale.Count > 0)
        {
            aircraft.Targets.NavigationRoute.RemoveAll(t => stale.Contains(t.Name));
        }
    }

    /// <summary>
    /// Sets <see cref="AircraftProcedure.DestinationRunway"/> and extends the active STAR route
    /// for that runway (scrubbing fixes from other runway transitions first).
    /// </summary>
    internal static void SyncDestinationRunwayWithActiveStar(AircraftState aircraft, string runwayDesignator)
    {
        aircraft.Procedure.DestinationRunway = runwayDesignator;
        ExtendActiveStarWithRunwayTransition(aircraft, runwayDesignator);
    }

    /// <summary>
    /// Extends an aircraft's active NavigationRoute with the runway-transition fixes for the
    /// given <paramref name="runwayDesignator"/> on its active STAR. No-op when the aircraft
    /// has no active STAR, no destination airport, or the STAR has no transition for the
    /// runway. The transition joins at the last of its fixes the route already holds (see
    /// <see cref="SelectRunwayTransitionLegsToAppend"/>), so a fix the aircraft has already
    /// flown is never re-added behind it.
    /// </summary>
    internal static void ExtendActiveStarWithRunwayTransition(AircraftState aircraft, string runwayDesignator)
    {
        if (string.IsNullOrEmpty(aircraft.Procedure.ActiveStarId) || string.IsNullOrEmpty(aircraft.FlightPlan.Destination))
        {
            return;
        }

        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, aircraft.Procedure.ActiveStarId);
        if (star is null)
        {
            return;
        }

        CifpTransition? transition = LookupRunwayTransition(star.RunwayTransitions, runwayDesignator);
        if (transition is null || transition.Legs.Count == 0)
        {
            return;
        }

        RemoveStaleStarRunwayTransitionFixes(aircraft, star, transition);

        List<string> routeNames = [.. aircraft.Targets.NavigationRoute.Select(t => t.Name)];
        List<CifpLeg> newLegs = SelectRunwayTransitionLegsToAppend(transition.Legs, star, routeNames);
        if (newLegs.Count == 0)
        {
            return;
        }

        aircraft.Targets.StripClearanceLimitDirect();
        foreach (NavigationTarget target in DepartureClearanceHandler.ResolveLegsToTargets(newLegs))
        {
            aircraft.Targets.NavigationRoute.Add(target);
        }
    }

    /// <summary>
    /// The legs of <paramref name="star"/>'s runway transition that <paramref name="routeNames"/> still has to fly: the
    /// legs after the last transition leg whose fix the route already holds, so a transition fix the aircraft has
    /// already flown is never appended behind it. With no transition fix on the route, the whole transition comes back
    /// when the route is empty (nothing has been flown into it yet) or still ends on the STAR's common route; a route
    /// that has already run past the transition gets nothing.
    /// </summary>
    internal static List<CifpLeg> SelectRunwayTransitionLegsToAppend(
        IReadOnlyList<CifpLeg> transitionLegs,
        CifpStarProcedure star,
        IReadOnlyList<string> routeNames
    )
    {
        List<CifpLeg> fixLegs = [.. transitionLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier))];
        if (fixLegs.Count == 0)
        {
            return [];
        }

        var onRoute = new HashSet<string>(routeNames, StringComparer.OrdinalIgnoreCase);
        int joinIndex = fixLegs.FindLastIndex(l => onRoute.Contains(l.FixIdentifier!));
        if (joinIndex >= 0)
        {
            List<CifpLeg> tail = fixLegs.GetRange(joinIndex + 1, fixLegs.Count - joinIndex - 1);
            Log.LogDebug(
                "{Star} runway transition joins the route at {Fix} (transition leg {Index}); appending {Appended}",
                star.ProcedureId,
                fixLegs[joinIndex].FixIdentifier,
                joinIndex,
                DescribeLegFixes(tail)
            );
            return tail;
        }

        // An empty route holds nothing the aircraft has already flown, so it ends before the transition. The spawn
        // caller builds one from a navigation path; the live caller does not reach here with an empty route, because
        // FlightPhysics.ClearProcedureState nulls ActiveStarId when the route's last fix sequences, which returns
        // ExtendActiveStarWithRunwayTransition before this lookup.
        bool endsBeforeTransition = routeNames.Count == 0 || IsOnStarCommonRoute(star, routeNames[^1]);
        Log.LogDebug(
            "{Star} runway transition has no fix on the route, which ends at {LastFix} (before the transition: {Before}); appending {Appended}",
            star.ProcedureId,
            routeNames.Count > 0 ? routeNames[^1] : "<no route>",
            endsBeforeTransition,
            DescribeLegFixes(endsBeforeTransition ? fixLegs : [])
        );
        return endsBeforeTransition ? fixLegs : [];
    }

    /// <summary>
    /// Whether <paramref name="fixName"/> is a fix of the STAR's common route — the body between its enroute transitions
    /// and its runway transitions.
    /// </summary>
    private static bool IsOnStarCommonRoute(CifpStarProcedure star, string fixName) =>
        star.CommonLegs.Any(l => !string.IsNullOrEmpty(l.FixIdentifier) && fixName.Equals(l.FixIdentifier, StringComparison.OrdinalIgnoreCase));

    private static string DescribeLegFixes(IReadOnlyList<CifpLeg> legs) =>
        legs.Count == 0 ? "nothing" : string.Join(' ', legs.Select(l => l.FixIdentifier));

    /// <summary>
    /// Filters NavigationTargets to those ahead of the aircraft (within ±90° of heading),
    /// starting from the nearest such target.
    /// </summary>
    private static List<NavigationTarget> FindTargetsAhead(AircraftState aircraft, List<NavigationTarget> targets)
    {
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < targets.Count; i++)
        {
            double bearing = GeoMath.BearingTo(aircraft.Position, targets[i].Position);
            double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, targets[i].Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            return targets;
        }

        return targets.GetRange(bestIdx, targets.Count - bestIdx);
    }

    /// <summary>
    /// Find the subset of STAR body fixes ahead of the aircraft (within ±90° of heading),
    /// starting from the nearest such fix. Prevents U-turns to fixes behind the aircraft.
    /// </summary>
    private static List<string> FindStarFixesAhead(AircraftState aircraft, IReadOnlyList<string> bodyFixes)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < bodyFixes.Count; i++)
        {
            (double Lat, double Lon)? pos = navDb.GetFixPosition(bodyFixes[i]);
            if (pos is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            // No fixes ahead — use first fix as fallback
            return [.. bodyFixes];
        }

        return [.. bodyFixes.Skip(bestIdx)];
    }

    internal static CommandResult DispatchJawy(JoinAirwayCommand cmd, AircraftState aircraft)
    {
        CommandResult planned = PlanAirwayIntercept(aircraft, cmd, out AirwayInterceptPlan plan);
        if (!planned.Success)
        {
            return planned;
        }

        // Block 0 (immediate): fly present heading (to allow intercept)
        ClearRouteApplyingSpeedReverts(aircraft);
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on segment intercept, navigate the airway fix sequence
        aircraft.Queue.Blocks.Add(BuildAirwayInterceptBlock(cmd, plan));

        return planned;
    }

    /// <summary>
    /// The bracketing segment a JAWY intercept flies: the trigger's origin and radial, and the airway fix
    /// sequence to navigate once the segment is intercepted. A restored block's plan is rebuilt from the block's
    /// saved trigger, not from the aircraft's restored position.
    /// </summary>
    internal readonly record struct AirwayInterceptPlan(
        string InterceptFixName,
        double InterceptFixLat,
        double InterceptFixLon,
        int SegmentRadial,
        List<NavigationTarget> NavTargets,
        List<string> RemainingFixes
    );

    /// <summary>
    /// The block a JAWY leaves queued behind itself: on intercepting the segment, navigate the remaining fixes.
    /// Built here for the same reason as <see cref="BuildJradoInterceptBlock"/>.
    /// </summary>
    internal static CommandBlock BuildAirwayInterceptBlock(JoinAirwayCommand cmd, AirwayInterceptPlan plan)
    {
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = plan.InterceptFixName,
                FixLat = plan.InterceptFixLat,
                FixLon = plan.InterceptFixLon,
                TrueRadial = plan.SegmentRadial,
            },
            ApplyAction = (ac, _) =>
            {
                ac.Targets.AssignedMagneticHeading = null;
                List<NavigationTarget> holders = TakePendingSpeedReverts(ac.Targets.NavigationRoute);
                ac.Targets.NavigationRoute.Clear();
                foreach (NavigationTarget target in plan.NavTargets)
                {
                    ac.Targets.NavigationRoute.Add(target);
                }

                ApplyTakenSpeedReverts(ac, holders);
                return new CommandResult(true);
            },
            Description = $"intercept {cmd.AirwayId}: DCT {string.Join(" ", plan.RemainingFixes.Select(FixDisplay))}",
            NaturalDescription = $"On {cmd.AirwayId}: proceed via {string.Join(" ", plan.RemainingFixes.Select(FixDisplay))}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Navigation });
        return block;
    }

    /// <summary>Plans a JAWY intercept, or returns the refusal the dispatch hands straight back.</summary>
    private static CommandResult PlanAirwayIntercept(AircraftState aircraft, JoinAirwayCommand cmd, out AirwayInterceptPlan plan)
    {
        plan = default;
        NavigationDatabase navDb = NavigationDatabase.Instance;
        IReadOnlyList<string>? airwayFixes = navDb.GetAirwayFixes(cmd.AirwayId);
        if (airwayFixes is null || airwayFixes.Count == 0)
        {
            return new CommandResult(false, $"Unknown airway: {cmd.AirwayId}");
        }

        // Find the bracketing segment: the fix behind and fix ahead of the aircraft
        (int behindIdx, int aheadIdx) = AirwayBracketing.Find(navDb, aircraft.Position, aircraft.TrueHeading, airwayFixes);
        if (aheadIdx < 0)
        {
            return new CommandResult(false, $"No navigable segment found on {cmd.AirwayId}");
        }

        // Resolve ahead fix position (guaranteed non-null since AirwayBracketing.Find validated it)
        (double Lat, double Lon) aheadPos = navDb.GetFixPosition(airwayFixes[aheadIdx])!.Value;

        // Determine the segment course to intercept
        double segmentCourse;
        double interceptFixLat;
        double interceptFixLon;
        string interceptFixName;

        if (behindIdx >= 0)
        {
            (double Lat, double Lon) behindPos = navDb.GetFixPosition(airwayFixes[behindIdx])!.Value;
            segmentCourse = GeoMath.BearingTo(behindPos.Lat, behindPos.Lon, aheadPos.Lat, aheadPos.Lon);
            // Use behind fix as the radial origin — aircraft intercepts the radial FROM behind fix TO ahead fix
            interceptFixLat = behindPos.Lat;
            interceptFixLon = behindPos.Lon;
            interceptFixName = airwayFixes[behindIdx];
        }
        else
        {
            // No fix behind — aircraft is before the first fix. Direct to first fix.
            segmentCourse = GeoMath.BearingTo(aircraft.Position, new LatLon(aheadPos.Lat, aheadPos.Lon));
            interceptFixLat = aheadPos.Lat;
            interceptFixLon = aheadPos.Lon;
            interceptFixName = airwayFixes[aheadIdx];
        }

        int segmentRadial = (int)Math.Round(segmentCourse) % 360;

        // Determine direction of travel along the airway (forward or reverse)
        bool reversed = behindIdx >= 0 && behindIdx > aheadIdx;

        // Build remaining fix list from ahead fix onward (in the direction of travel)
        List<string> remainingFixes = FixesFrom(airwayFixes, aheadIdx, forward: !reversed);

        // Build NavigationTargets for the remaining fixes
        List<NavigationTarget> navTargets = ResolveFixTargets(remainingFixes);

        if (navTargets.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes on {cmd.AirwayId}");
        }

        plan = new AirwayInterceptPlan(interceptFixName, interceptFixLat, interceptFixLon, segmentRadial, navTargets, remainingFixes);

        string fixListStr = string.Join(" ", remainingFixes.Select(FixDisplay));
        return CommandDispatcher.Ok($"Fly present heading, intercept {cmd.AirwayId}: {fixListStr}");
    }

    /// <summary>
    /// The candidate fix sequences a restored JAWY block could have been planned with, given the trigger it was
    /// saved with. The sequence is rebuilt from that trigger and never re-planned against the aircraft's position:
    /// <see cref="AirwayBracketing.Find"/> can pick a different fix once earlier fixes pass abeam, and finds no
    /// segment at all once the aircraft is past the airway's last fix ahead.
    ///
    /// The trigger names the segment origin, which is either the fix behind the aircraft — the far end is then a
    /// neighbour of it — or, when the live plan found no fix behind, the first fix still to fly, with the whole
    /// forward sequence from it. The trigger alone cannot say which: its radial is a segment course in the first
    /// case and the bearing from the aircraft to the origin in the second, and an aircraft on the airway's extended
    /// centreline makes the two agree. So all three sequences are offered, and the caller keeps the one whose
    /// rebuilt description matches the block's saved one.
    /// </summary>
    internal static IEnumerable<AirwayInterceptPlan> PlanAirwayInterceptsFromTrigger(JoinAirwayCommand cmd, BlockTrigger trigger)
    {
        if (trigger.FixName is not { } originName || trigger.FixLat is not { } originLat || trigger.FixLon is not { } originLon)
        {
            yield break;
        }

        IReadOnlyList<string>? airwayFixes = NavigationDatabase.Instance.GetAirwayFixes(cmd.AirwayId);
        if (airwayFixes is null || airwayFixes.Count == 0)
        {
            yield break;
        }

        int originIdx = IndexOfFix(airwayFixes, originName);
        if (originIdx < 0)
        {
            yield break;
        }

        foreach (List<string> remainingFixes in RemainingFixCandidates(airwayFixes, originIdx))
        {
            List<NavigationTarget> navTargets = ResolveFixTargets(remainingFixes);
            if (navTargets.Count == 0)
            {
                continue;
            }

            // The serialized trigger is never re-derived, so the plan carries its own origin and radial only to keep
            // the plan's shape uniform with the live one's.
            yield return new AirwayInterceptPlan(originName, originLat, originLon, trigger.TrueRadial ?? 0, navTargets, remainingFixes);
        }
    }

    /// <summary>
    /// The sequences a trigger's origin can start: forward from the neighbour after it, backward from the neighbour
    /// before it, and forward from the origin itself when it is the first fix still to fly.
    /// </summary>
    private static IEnumerable<List<string>> RemainingFixCandidates(IReadOnlyList<string> airwayFixes, int originIdx)
    {
        if (originIdx + 1 < airwayFixes.Count)
        {
            yield return FixesFrom(airwayFixes, originIdx + 1, forward: true);
        }

        if (originIdx > 0)
        {
            yield return FixesFrom(airwayFixes, originIdx - 1, forward: false);
        }

        yield return FixesFrom(airwayFixes, originIdx, forward: true);
    }

    /// <summary>The airway fix list from <paramref name="startIdx"/> to its end (or to its start, walking back).</summary>
    private static List<string> FixesFrom(IReadOnlyList<string> airwayFixes, int startIdx, bool forward)
    {
        var fixes = new List<string>();
        if (forward)
        {
            for (int i = startIdx; i < airwayFixes.Count; i++)
            {
                fixes.Add(airwayFixes[i]);
            }
        }
        else
        {
            for (int i = startIdx; i >= 0; i--)
            {
                fixes.Add(airwayFixes[i]);
            }
        }

        return fixes;
    }

    private static int IndexOfFix(IReadOnlyList<string> airwayFixes, string fixName)
    {
        for (int i = 0; i < airwayFixes.Count; i++)
        {
            if (string.Equals(airwayFixes[i], fixName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static List<NavigationTarget> ResolveFixTargets(IReadOnlyList<string> fixNames)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        var targets = new List<NavigationTarget>();
        foreach (string fixName in fixNames)
        {
            (double Lat, double Lon)? pos = navDb.GetFixPosition(fixName);
            if (pos is not null)
            {
                targets.Add(new NavigationTarget { Name = fixName, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
            }
        }

        return targets;
    }

    /// <summary>The altitude at and below which a hold's leg is 1 minute, in feet MSL (AIM 5-3-8(j)(4)(a)).</summary>
    internal const double StandardHoldLegThresholdFt = 14000;

    /// <summary>The leg of an altitude-standard hold at or below <see cref="StandardHoldLegThresholdFt"/>, in minutes.</summary>
    internal const double StandardHoldLegMinutesAtOrBelow = 1.0;

    /// <summary>The leg of an altitude-standard hold above <see cref="StandardHoldLegThresholdFt"/>, in minutes.</summary>
    internal const double StandardHoldLegMinutesAbove = 1.5;

    /// <summary>The leg length an omitted <c>HOLDP</c> leg flies at <paramref name="altitudeFt"/> MSL.</summary>
    internal static double StandardHoldLegMinutes(double altitudeFt) =>
        altitudeFt <= StandardHoldLegThresholdFt ? StandardHoldLegMinutesAtOrBelow : StandardHoldLegMinutesAbove;

    /// <summary>
    /// The leg a <c>HOLDP</c> flies: the controller's leg when given, else the altitude standard at <paramref name="altitudeFt"/>
    /// (always minute-based).
    /// </summary>
    internal static (double LegLength, bool IsMinuteBased) ResolveHoldLeg(HoldingPatternCommand cmd, double altitudeFt) =>
        (cmd.LegLength ?? StandardHoldLegMinutes(altitudeFt), (cmd.LegLength is null) || cmd.IsMinuteBased);

    /// <summary>
    /// The true inbound course, in whole degrees, a <c>HOLDP</c>'s magnetic course flies. The controller's holding course is
    /// magnetic (7110.65 1-2-1): referenced to the navaid's variation of record when the fix is a navaid, else to the modelled
    /// variation at the fix.
    /// </summary>
    internal static int HoldInboundTrueCourse(HoldingPatternCommand cmd)
    {
        double declination = NavigationDatabase.Instance.GetPublishedCourseDeclination(cmd.FixName, string.Empty, new LatLon(cmd.FixLat, cmd.FixLon));
        TrueHeading inboundTrue = new MagneticHeading(cmd.InboundCourse).ToTrue(declination);
        return (int)Math.Round(inboundTrue.Degrees) % 360;
    }

    internal static CommandResult DispatchHoldingPattern(HoldingPatternCommand cmd, AircraftState aircraft)
    {
        // An omitted leg is the altitude standard, fixed when the hold is issued: the assigned altitude, else the present one.
        (double legLength, bool isMinuteBased) = ResolveHoldLeg(cmd, aircraft.Targets.AssignedAltitude ?? aircraft.Altitude);

        // The phase flies a true course; the readback keeps the magnetic one.
        var phase = new HoldingPatternPhase
        {
            FixName = cmd.FixName,
            FixLat = cmd.FixLat,
            FixLon = cmd.FixLon,
            InboundCourse = HoldInboundTrueCourse(cmd),
            LegLength = legLength,
            IsMinuteBased = isMinuteBased,
            Direction = cmd.Direction,
            Entry = cmd.Entry,
        };

        InstallHoldingPhase(aircraft, phase);

        string dirStr = cmd.Direction == TurnDirection.Left ? "left" : "right";
        string legStr = $"{legLength.ToString(CultureInfo.InvariantCulture)}{(isMinuteBased ? "min" : "nm")}";
        return CommandDispatcher.Ok($"Hold at {FixDisplay(cmd.FixName)}, {cmd.InboundCourse:D3} inbound, {dirStr} turns, {legStr} legs");
    }

    /// <summary>
    /// Replaces the aircraft's phases with <paramref name="phase"/> and starts it, keeping the assigned runway.
    /// </summary>
    internal static void InstallHoldingPhase(AircraftState aircraft, HoldingPatternPhase phase)
    {
        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (aircraft.Phases is not null)
        {
            // No route to the room's active runways: this handler is not given the dispatch context.
            PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = runway is not null ? new PhaseList { AssignedRunway = runway } : new PhaseList();
        aircraft.Phases.Add(phase);

        // No route to the room's active runways: this handler is not given the dispatch context.
        PhaseContext startCtx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
        aircraft.Phases.Start(startCtx);
    }

    internal static CommandResult DispatchJfac(JoinFinalApproachCourseCommand cmd, AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string airport = CommandDispatcher.ResolveAirport(aircraft);
        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for approach");
        }

        // Auto-resolve: when no approach ID given, try ExpectedApproach first, then assigned runway
        string? approachId = cmd.ApproachId;
        if (approachId is null)
        {
            // A visual approach has no published final approach course to join.
            if (ApproachIds.TryParseVisual(aircraft.Approach.Expected, out _))
            {
                return new CommandResult(false, "Unable, expecting a visual approach; clear it with CVA");
            }

            approachId = aircraft.Approach.Expected ?? aircraft.Procedure.DestinationRunway ?? aircraft.Procedure.DepartureRunway;
            if (approachId is null)
            {
                return new CommandResult(false, "No approach ID and no runway assigned — cannot auto-resolve");
            }
        }

        string? resolvedId = navDb.ResolveApproachId(airport, approachId);
        if (resolvedId is null)
        {
            return new CommandResult(false, $"Unknown approach: {approachId} at {airport}");
        }

        CifpApproachProcedure? procedure = navDb.GetApproach(airport, resolvedId);
        if (procedure?.Runway is null)
        {
            return new CommandResult(false, $"No runway for approach {resolvedId}");
        }

        RunwayInfo? runway = navDb.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")} at {airport}");
        }

        // Ensure the runway designator matches the approach runway
        RunwayInfo approachRunway = runway.IsActiveEnd(procedure.Runway) ? runway : runway.ForApproach(procedure.Runway);

        FinalApproachCourseResult facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, navDb);
        TrueHeading finalCourse = facResult.Course;

        // JFAC/JLOC is a lateral "join the localizer" vector, not an approach clearance: it does
        // NOT cancel a previously assigned speed (7110.65 §5-7-4 / §5-7-1.d only on approach
        // clearance, resume-normal, or descend-via). Keep the assigned speed and any STAR
        // crossing-speed ceiling — the aircraft holds them through the intercept until CAPP.

        // The vector the join was appended to ("FH 220, JLOC") is the angle the intercept is judged
        // against, so it is read before the join drops it.
        MagneticHeading? interceptHeading = aircraft.Targets.AssignedMagneticHeading;

        // Clear assigned heading — approach takes over steering
        aircraft.Targets.AssignedMagneticHeading = null;
        List<NavigationTarget> pendingReverts = TakePendingSpeedReverts(aircraft.Targets.NavigationRoute);
        aircraft.Targets.NavigationRoute.Clear();

        // Clear existing phases
        if (aircraft.Phases is not null)
        {
            // No route to the room's active runways: this handler is not given the dispatch context.
            PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
            aircraft.Phases.Clear(ctx);
        }

        // A join flown on own navigation, as JRADI is: a crossing pending on the dropped route ends as if its fix were
        // crossed, so the speed it replaced comes back.
        ApplyTakenSpeedReverts(aircraft, pendingReverts);

        ApproachCommandHandler.ClearPendingApproach(aircraft);

        // Build phase sequence: InterceptCourse → FinalApproach → Landing.
        // RelaxedJoin: a "join the localizer/FAC" instruction (often appended to a vector,
        // e.g. "FH 220, JLOC") arms the join — the aircraft keeps flying its assigned heading
        // and turns onto the localizer when it intercepts, at any cut, never busting through
        // (7110.65 §5-9-2 limits the controller's vector, not this relaxed join). Kept distinct
        // from ForcedIntercept so the glideslope gate stays scoped to PTACF: the lateral-only
        // hold means no descent until CAPP.
        // The intercept's threshold is the landing threshold: its distance is scored against the
        // approach gate, which the P/CG measures from there.
        LatLon interceptThreshold = LandingThreshold.Resolve(approachRunway, aircraft.Ground.Layout);
        var interceptPhase = new InterceptCoursePhase
        {
            FinalApproachCourse = finalCourse,
            ThresholdLat = interceptThreshold.Lat,
            ThresholdLon = interceptThreshold.Lon,
            ApproachId = resolvedId,
            AssignedInterceptHeading = interceptHeading,
            RelaxedJoin = true,
        };

        var finalPhase = new FinalApproachPhase();
        bool isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        Phase landingPhase = isHeliApch ? new HelicopterLandingPhase() : new LandingPhase();

        var clearance = new ApproachClearance
        {
            TransitionName = null,
            ApproachId = resolvedId,
            AirportCode = airport,
            RunwayId = procedure.Runway,
            FinalApproachCourse = finalCourse,
            FinalApproachAnchorLat = facResult.AnchorLat,
            FinalApproachAnchorLon = facResult.AnchorLon,
            Procedure = procedure,
            MapAltitudeFt = ApproachCommandHandler.ExtractMapAltitude(procedure),
            MapDistanceNm = ApproachCommandHandler.ExtractMapDistance(procedure, approachRunway),
            // Lateral intercept only: hold altitude on the final approach course until CAPP.
            LateralInterceptOnly = true,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;

        aircraft.Phases.Add(interceptPhase);
        aircraft.Phases.Add(finalPhase);
        aircraft.Phases.Add(landingPhase);

        // No route to the room's active runways: this handler is not given the dispatch context.
        PhaseContext startCtx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
        aircraft.Phases.Start(startCtx);

        return CommandDispatcher.Ok(
            $"Join final approach course, {resolvedId}, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
        );
    }

    /// <summary>
    /// Whether a DVIA or CVIA of <paramref name="transmissionId"/> has applied and is still in force. Never true for id
    /// 0, which is no transmission.
    /// </summary>
    internal static bool ViaAppliedInTransmission(AircraftState aircraft, int transmissionId) =>
        (transmissionId != 0)
        && ((aircraft.Procedure.StarViaTransmissionId == transmissionId) || (aircraft.Procedure.ClimbViaTransmissionId == transmissionId));

    /// <summary>The id to stamp for <paramref name="transmissionId"/>: none for id 0, which is no transmission.</summary>
    private static int? StampedTransmission(int transmissionId) => (transmissionId == 0) ? null : transmissionId;

    internal static CommandResult DispatchClimbVia(ClimbViaCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        // "Climb via the [SID]" only requires the SID to be in the cleared route (AIM 5-2-9). If no SID
        // is active yet — e.g. the aircraft was given runway heading (CTO RH) or vectored off and then
        // re-cleared DCT to a downstream SID fix — activate the one in the filed route and overlay its
        // published crossing restrictions onto the live lateral route.
        bool inForce = TryPutClimbViaInForce(
            aircraft,
            ctx.TransmissionId,
            fromTdls: false,
            () =>
            {
                SupersedeEarlierCrossingRestrictions(aircraft, ctx.TransmissionId, ActiveSidRestrictions);
                CancelEarlierAssignedSpeed(aircraft, ctx.TransmissionId);
                // A bare climb via cancels an interim altitude assigned before it and climbs to the SID's top altitude
                // (7110.65 4-5-7.h.10 Example 1, h.11 NOTE 1; AIM 5-2-9.h.8).
                aircraft.Targets.AssignedAltitude = null;
                aircraft.Procedure.SpeedRestrictionsDeleted = false;
                return ResolveClimbViaTarget(cmd, aircraft, ctx.ArtccConfig);
            }
        );
        if (!inForce)
        {
            return new CommandResult(false, "No active SID — climb via requires a SID in the filed route");
        }

        aircraft.Targets.TargetAltitude = FlightPhysics.ClimbViaAltitude(aircraft);

        if (cmd.Altitude is not null)
        {
            return CommandDispatcher.Ok($"Climb via SID, except maintain {cmd.Altitude:N0}");
        }

        return CommandDispatcher.Ok("Climb via SID");
    }

    /// <summary>
    /// Puts a climb via in force from takeoff for the climb-via text of the SID transition's TDLS default, the via the
    /// departure clearance read to the crew before anything said on the ground: unlike a <c>CVIA</c>, it supersedes no
    /// crossing restriction given on the ground — activating the SID (<see cref="TryActivateFiledSid"/>) does overlay its
    /// published restrictions onto the route fixes they match — and cancels no speed or altitude assigned before it. Its top is
    /// <paramref name="exceptMaintainFt"/>, an "except maintain"; with none, the SID's published climb via top
    /// (<see cref="PublishedClimbViaTopFt"/>). Never below present altitude rounded up to 100 ft. The via is marked as the
    /// TDLS default (<see cref="AircraftProcedure.ClimbViaTopFromTdls"/>). False, with nothing changed, when no SID in the
    /// filed route can be activated.
    /// </summary>
    internal static bool TryPutTdlsClimbViaInForce(AircraftState aircraft, int transmissionId, int? exceptMaintainFt) =>
        TryPutClimbViaInForce(
            aircraft,
            transmissionId,
            fromTdls: true,
            () =>
            {
                int floor = PresentAltitudeFloorFt(aircraft);
                if (exceptMaintainFt is { } assigned)
                {
                    return (Math.Max(assigned, floor), assigned);
                }

                if (PublishedClimbViaTopFt(aircraft) is { } published)
                {
                    return (Math.Max(published, floor), null);
                }

                Log.LogWarning(
                    "[ClimbVia] {Callsign}: SID {Sid} not found for {Departure}; holding {Floor}",
                    aircraft.Callsign,
                    aircraft.Procedure.ActiveSidId,
                    aircraft.FlightPlan.Departure,
                    floor
                );
                return (floor, null);
            }
        );

    /// <summary>
    /// Puts a climb via in force: activates the filed SID when none is active, then resolves the top with
    /// <paramref name="resolveTop"/> (the caller's own steps that must precede it run there, after the SID is active) and
    /// writes the via state, whether it is the TDLS default (<paramref name="fromTdls"/>), and the first constrained fix; a
    /// caller that wants the target altitude set at once writes it itself. False, with nothing changed, when no SID in the
    /// filed route can be activated.
    /// </summary>
    private static bool TryPutClimbViaInForce(
        AircraftState aircraft,
        int transmissionId,
        bool fromTdls,
        Func<(int TargetFt, int? ExceptMaintainFt)> resolveTop
    )
    {
        if ((aircraft.Procedure.ActiveSidId is null) && !TryActivateFiledSid(aircraft, transmissionId))
        {
            return false;
        }

        (int target, int? exceptMaintain) = resolveTop();
        aircraft.Procedure.SidViaMode = true;
        aircraft.Procedure.ClimbViaTopFromTdls = fromTdls;
        aircraft.Procedure.SidViaCeiling = exceptMaintain;
        aircraft.Procedure.ClimbViaTargetFt = target;
        aircraft.Procedure.ClimbViaTransmissionId = StampedTransmission(transmissionId);
        // A via clearance is a newer altitude clearance: the TDLS maintain a bare departure clearance stored no longer
        // governs after it, even once the via itself has ended (7110.65 4-5-7.h.11 NOTE 1).
        aircraft.Procedure.SidInitialAltitudeFt = null;
        // Reset procedural speed memory; ApplyFirstConstrainedFix will repopulate
        // it from the next constrained fix in the route.
        aircraft.Procedure.LastProcedureSpeedKts = null;
        ApplyFirstConstrainedFix(aircraft);
        return true;
    }

    /// <summary>
    /// The altitude a climb via climbs to, and the ATC-assigned "except maintain" altitude behind it, if any:
    /// <list type="number">
    /// <item><c>CVIA nnn</c>'s altitude, an "except maintain";</item>
    /// <item>else the SID's TDLS top for the departure airport and filed transition
    /// (<see cref="ArtccConfigResolver.GetSidTdlsTopAltitude"/>): the climb-via text's "EXC MAINT" altitude, an "except
    /// maintain" (AIM 5-2-9.h.9), or the transition's initial altitude; a TDLS value is never raised by a published
    /// altitude, floors included;</item>
    /// <item>else the SID's published climb via top (<see cref="PublishedClimbViaTopFt"/>): the highest ceiling on its
    /// flown legs, or FL190 when there is none or an at-or-above floor sits above it;</item>
    /// <item>else (the SID cannot be resolved) present altitude, with a warning.</item>
    /// </list>
    /// Never below present altitude rounded up to 100 ft, since a via clearance never commands a descent (a descent takes
    /// DM). The TDLS initial-altitude menu and the maintain a bare CTO stored (<see cref="AircraftProcedure.SidInitialAltitudeFt"/>)
    /// never take part.
    /// </summary>
    private static (int TargetFt, int? ExceptMaintainFt) ResolveClimbViaTarget(
        ClimbViaCommand cmd,
        AircraftState aircraft,
        ArtccConfigRoot? artccConfig
    )
    {
        int floor = PresentAltitudeFloorFt(aircraft);
        if (cmd.Altitude is { } assigned)
        {
            return (Math.Max(assigned, floor), assigned);
        }

        string departure = aircraft.FlightPlan.Departure ?? "";
        if (artccConfig.GetSidTdlsTopAltitude(departure, aircraft.Procedure.ActiveSidId, FiledSidTransition(aircraft)) is { } tdls)
        {
            return (Math.Max(tdls.AltitudeFt, floor), tdls.IsExceptMaintain ? tdls.AltitudeFt : null);
        }

        if (PublishedClimbViaTopFt(aircraft) is { } published)
        {
            return (Math.Max(published, floor), null);
        }

        Log.LogWarning(
            "[ClimbVia] {Callsign}: SID {Sid} not found for {Departure}; holding {Floor}",
            aircraft.Callsign,
            aircraft.Procedure.ActiveSidId,
            departure,
            floor
        );
        return (floor, null);
    }

    /// <summary>The filed route's tokens, split at spaces and at the dots of a "SID.TRANSITION" form.</summary>
    private static string[] FiledRouteTokens(AircraftState aircraft) =>
        (aircraft.FlightPlan.Route ?? "").Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The active SID's filed enroute transition: the filed route's token after the SID's; null when there is none. The SID
    /// token is the active id itself, else the first token that ends in a digit and shares the id's base name without its
    /// trailing version digits (a SID renumbered by a navdata cycle and still filed under the old version); a bare fix or
    /// navaid such as OAK never matches the SID OAK7.
    /// </summary>
    private static string? FiledSidTransition(AircraftState aircraft)
    {
        string[] tokens = FiledRouteTokens(aircraft);
        string? activeSidId = aircraft.Procedure.ActiveSidId;
        int sid = Array.FindIndex(tokens, t => t.Equals(activeSidId, StringComparison.OrdinalIgnoreCase));
        if ((sid < 0) && (activeSidId is not null))
        {
            string baseName = NavigationDatabase.StripTrailingDigits(activeSidId);
            sid = Array.FindIndex(
                tokens,
                t => char.IsDigit(t[^1]) && NavigationDatabase.StripTrailingDigits(t).Equals(baseName, StringComparison.OrdinalIgnoreCase)
            );
        }

        return ((sid >= 0) && (sid + 1 < tokens.Length)) ? tokens[sid + 1] : null;
    }

    /// <summary>FL190, the top a bare climb via flies to when neither TDLS nor a published ceiling gives one.</summary>
    internal const int ClimbViaDefaultTopFt = 19000;

    /// <summary>Present altitude rounded up to 100 ft: the lowest top a via clearance can have, since it never commands a descent.</summary>
    private static int PresentAltitudeFloorFt(AircraftState aircraft) => (int)(Math.Ceiling(Math.Round(aircraft.Altitude) / 100.0) * 100);

    /// <summary>
    /// The top a bare climb via flies to from what the active SID publishes on the legs <paramref name="aircraft"/> flies:
    /// the highest ceiling (an at, a glideslope intercept, an at-or-below, or a between's upper bound), unless an
    /// at-or-above floor on those legs sits above it; with no ceiling, or a floor above it, <see cref="ClimbViaDefaultTopFt"/>.
    /// An at-or-above is a floor under a climb via, never its top. Null when the SID cannot be resolved.
    /// </summary>
    internal static int? PublishedClimbViaTopFt(AircraftState aircraft)
    {
        if (ResolveActiveSid(aircraft) is not { } sid)
        {
            return null;
        }

        int ceiling = 0;
        int floor = 0;
        foreach (CifpAltitudeRestriction restriction in FlownSidLegs(sid, aircraft).Select(l => l.Altitude).OfType<CifpAltitudeRestriction>())
        {
            if (FlightPhysics.IsClimbCeiling(restriction))
            {
                ceiling = Math.Max(ceiling, Math.Max(restriction.Altitude1Ft, restriction.Altitude2Ft ?? 0));
            }
            else if (restriction.Type == CifpAltitudeRestrictionType.AtOrAbove)
            {
                floor = Math.Max(floor, restriction.Altitude1Ft);
            }
        }

        return ((ceiling > 0) && (floor <= ceiling)) ? ceiling : ClimbViaDefaultTopFt;
    }

    /// <summary>
    /// Works out again the top of a TDLS-default climb via (<see cref="AircraftProcedure.ClimbViaTopFromTdls"/>) for the
    /// departure runway now set: its "except maintain" if any, else the SID's published climb via top, never below
    /// present altitude rounded up to 100 ft. Changes nothing else.
    /// </summary>
    internal static void ReResolveTdlsClimbViaTop(AircraftState aircraft)
    {
        int floor = PresentAltitudeFloorFt(aircraft);
        aircraft.Procedure.ClimbViaTargetFt = Math.Max(aircraft.Procedure.SidViaCeiling ?? PublishedClimbViaTopFt(aircraft) ?? floor, floor);
    }

    /// <summary>
    /// Whether the active SID can be cleared as a climb via: it resolves, it is not a radar-vectors SID
    /// (<see cref="DepartureClearanceHandler.IsRadarVectorsSid"/> on the runway transition for
    /// <see cref="AircraftProcedure.DepartureRunway"/>, or on any runway transition when no transition is identified for it:
    /// the runway is unknown, or the SID has no transition for it), and it publishes a crossing restriction at a fix on the
    /// legs <paramref name="aircraft"/> flies (<see cref="PublishedSidRestrictions"/>; a CA/VA/VI leg's end altitude is
    /// none). A radar-vectors SID, or one with no published crossing restriction, is never a climb-via clearance (7110.65
    /// 4-3-2.e.5; AIM 5-2-9.h.5).
    /// </summary>
    internal static bool ActiveSidPublishesClimbViaAltitudes(AircraftState aircraft)
    {
        if (ResolveActiveSid(aircraft) is not { } sid)
        {
            return false;
        }

        bool radarVectors =
            (FlownRunwayTransition(sid, aircraft) is { } flown) ? DepartureClearanceHandler.IsRadarVectorsSid(flown.Legs, sid.CommonLegs)
            : (sid.RunwayTransitions.Count == 0) ? DepartureClearanceHandler.IsRadarVectorsSid([], sid.CommonLegs)
            : sid.RunwayTransitions.Values.Any(t => DepartureClearanceHandler.IsRadarVectorsSid(t.Legs, sid.CommonLegs));
        return !radarVectors && PublishedSidRestrictions(sid, aircraft).Values.Any(t => t.AltitudeRestriction is not null);
    }

    private static CifpSidProcedure? ResolveActiveSid(AircraftState aircraft)
    {
        string? sidId = aircraft.Procedure.ActiveSidId;
        string? departure = aircraft.FlightPlan.Departure;
        return ((sidId is null) || string.IsNullOrEmpty(departure)) ? null : NavigationDatabase.Instance.GetSid(departure, sidId);
    }

    internal static CommandResult DispatchDescendVia(DescendViaCommand cmd, AircraftState aircraft, int transmissionId)
    {
        // DVIA SPD <speed> <fix> names a fix on the route; refuse one off it before anything changes.
        NavigationTarget? speedFix = null;
        if (cmd.Speed is not null && cmd.SpeedFixName is not null && cmd.SpeedFixLat is not null && cmd.SpeedFixLon is not null)
        {
            speedFix = aircraft.Targets.NavigationRoute.Find(f => string.Equals(f.Name, cmd.SpeedFixName, StringComparison.OrdinalIgnoreCase));
            if (speedFix is null)
            {
                return new CommandResult(false, $"Fix {FixDisplay(cmd.SpeedFixName)} not found in current route");
            }
        }

        // "Descend via the [STAR]" only requires the STAR to be in the cleared route (AIM 5-4-1.a.2); no separate join
        // step is needed. With no STAR active, activate the one in the filed route and overlay its published crossing
        // restrictions onto the live lateral route. With one active, overlay them again every time: on a route a "JARR"
        // join or an earlier descend via already constrained that rewrites the same published values, while the values
        // this transmission set are kept by the overlay's stamp checks and earlier ones by the supersede pass below.
        bool starReady;
        if (aircraft.Procedure.ActiveStarId is null)
        {
            starReady = TryActivateFiledStar(aircraft, transmissionId);
        }
        else
        {
            OverlayActiveStarRestrictions(aircraft, transmissionId);
            starReady = true;
        }

        if (!starReady)
        {
            return new CommandResult(false, "No active STAR — descend via requires a STAR in the filed route");
        }

        SupersedeEarlierCrossingRestrictions(aircraft, transmissionId, ActiveStarRestrictions);
        CancelEarlierAssignedSpeed(aircraft, transmissionId);
        aircraft.Procedure.StarViaMode = true;
        aircraft.Procedure.StarViaTransmissionId = StampedTransmission(transmissionId);
        aircraft.Procedure.StarViaFloor = cmd.Altitude;
        // A descend via ends any climb via still in force, so the STAR's restrictions drive the altitude.
        aircraft.Procedure.ClimbViaTargetFt = null;
        aircraft.Procedure.ClimbViaTransmissionId = null;
        aircraft.Procedure.SidViaCeiling = null;
        // SidViaMode switches descent planning off (FlightPhysics.UpdateDescentPlanning): a descend via leaves it.
        aircraft.Procedure.SidViaMode = false;
        aircraft.Procedure.ClimbViaTopFromTdls = false;
        // The via clearance supersedes any altitude assigned before it (converse of 7110.65 4-5-7.h.11 NOTE 1), and the
        // TDLS maintain a bare departure clearance stored, which must not come back once the via ends.
        aircraft.Targets.AssignedAltitude = null;
        aircraft.Procedure.SidInitialAltitudeFt = null;
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        // Reset procedural speed memory; ApplyFirstConstrainedFix will repopulate
        // it from the next constrained fix in the route.
        aircraft.Procedure.LastProcedureSpeedKts = null;
        ApplyFirstConstrainedFix(aircraft);

        // DVIA SPD <speed> <fix>: set the speed on the fix in place, so everything else on it survives. It is the
        // controller's speed, not a published one, so it takes this transmission's speed stamp as a CFIX speed does: a later
        // via or approach clearance gives the fix its published speed back, and crossing it is not remembered as a published
        // speed. It sets no altitude, so the fix's crossing stamp stays as it is.
        if ((speedFix is not null) && (cmd.Speed is { } speed))
        {
            speedFix.SpeedRestriction = new CifpSpeedRestriction(speed, CifpSpeedRestrictionType.AtOrBelow);
            speedFix.SpeedTransmissionId = StampedTransmission(transmissionId);

            return CommandDispatcher.Ok($"Descend via STAR, {speed} knots at {FixDisplay(speedFix.Name)}");
        }

        if (cmd.Altitude is not null)
        {
            return CommandDispatcher.Ok($"Descend via STAR, except maintain {cmd.Altitude:N0}");
        }

        return CommandDispatcher.Ok("Descend via STAR");
    }

    /// <summary>
    /// Overlays the active STAR's published crossing restrictions onto the live route, by fix name: a level STAR spawn
    /// puts the published speeds on it and no altitudes, and the descend via is what authorizes the altitudes (7110.65
    /// 5-7-1.d NOTE; AIM 5-4-1.a.2(a) NOTE 1). An active STAR NavData cannot resolve is logged and left un-overlaid.
    /// </summary>
    private static void OverlayActiveStarRestrictions(AircraftState aircraft, int transmissionId)
    {
        if (ResolveActiveStar(aircraft) is not { } star)
        {
            Log.LogWarning(
                "{Callsign}: DVIA could not resolve active STAR {Star} at {Dest}; published altitudes not overlaid",
                aircraft.Callsign,
                aircraft.Procedure.ActiveStarId,
                aircraft.FlightPlan.Destination
            );
            return;
        }

        OverlayStarRestrictions(aircraft, star, transmissionId);
    }

    /// <summary>
    /// Applies constraints from the first fix in the nav route that has an altitude
    /// or speed restriction. Called immediately when CVIA/DVIA is issued so the
    /// aircraft starts working toward the first constraint right away, rather than
    /// waiting to sequence through unconstrained fixes. A slowdown for its speed is left to
    /// the planner (<see cref="FlightPhysics.ApplyViaClearanceFixConstraints"/>).
    /// </summary>
    private static void ApplyFirstConstrainedFix(AircraftState aircraft)
    {
        foreach (NavigationTarget target in aircraft.Targets.NavigationRoute)
        {
            if (target.AltitudeRestriction is not null || target.SpeedRestriction is not null)
            {
                FlightPhysics.ApplyViaClearanceFixConstraints(aircraft, target);
                break;
            }
        }
    }

    /// <summary>
    /// Activates the STAR named in the aircraft's filed route when none is active yet, so a bare
    /// <c>DVIA</c> works without a prior <c>JARR</c> (the STAR being in the cleared route is the only
    /// real-world precondition). Sets <see cref="AircraftProcedure.ActiveStarId"/> and overlays the
    /// published CIFP altitude/speed restrictions onto the existing (already-sequenced) lateral route
    /// by fix name — it does not rebuild the route, so fixes the aircraft has already passed are not
    /// re-added. Returns false when the filed route contains no resolvable STAR.
    /// </summary>
    private static bool TryActivateFiledStar(AircraftState aircraft, int transmissionId)
    {
        if (FiledProcedureLookup.FindStar(NavigationDatabase.Instance, aircraft.FlightPlan.Destination, aircraft.FlightPlan.Route) is not { } filed)
        {
            return false;
        }

        OverlayStarRestrictions(aircraft, filed.Procedure, transmissionId);
        aircraft.Procedure.ActiveStarId = filed.Id;
        return true;
    }

    /// <summary>
    /// Copies each published altitude/speed restriction from <paramref name="star"/> onto the matching
    /// fix (by name) already present in the aircraft's navigation route, reusing the same leg→target
    /// resolution as <c>JARR</c> so the constraint semantics are identical (<see cref="OverlayPublishedRestrictions"/>).
    /// </summary>
    private static void OverlayStarRestrictions(AircraftState aircraft, CifpStarProcedure star, int transmissionId) =>
        OverlayPublishedRestrictions(aircraft.Targets.NavigationRoute, PublishedStarRestrictions(star, aircraft), transmissionId);

    /// <summary>
    /// Copies <paramref name="byName"/>'s published restrictions onto the route fixes of the same name, each axis on its
    /// own. A crossing altitude a CFIX in this same transmission stamped stays, and so does a speed this transmission set:
    /// the controller restated them with the via clearance. The other axis takes the published restriction, so an
    /// altitude-only CFIX still gets the fix's published speed. A fix that takes the published altitude back drops the
    /// altitude reverts an earlier crossing left with it; a speed's reverts stay, as that axis has its own stamp.
    /// <paramref name="route"/> is the live route for a STAR and the effective one for a SID (<see cref="EffectiveRoute"/>).
    /// </summary>
    private static void OverlayPublishedRestrictions(List<NavigationTarget> route, Dictionary<string, NavigationTarget> byName, int transmissionId)
    {
        foreach (NavigationTarget fix in route)
        {
            if (!byName.TryGetValue(fix.Name, out NavigationTarget? source))
            {
                continue;
            }

            if (!IsStampedBy(fix, transmissionId))
            {
                fix.AltitudeRestriction = source.AltitudeRestriction;
                fix.CrossingTransmissionId = null;
                fix.RevertAltitude = null;
                fix.RevertAssignedAltitude = null;
            }

            if (!IsSpeedStampedBy(fix, transmissionId))
            {
                fix.SpeedRestriction = source.SpeedRestriction;
            }
        }
    }

    /// <summary>
    /// A via clearance (descend via or climb via) supersedes a crossing restriction issued before it and not restated
    /// with it (7110.65 4-5-7.h.9, h.11 NOTES 1-2; AIM 5-2-9.h.8 Example 8). Every route fix loses its pending reverts
    /// (altitude, assigned altitude, speed, assigned speed), so passing it can no longer restore an altitude or speed
    /// assigned before the via clearance. Altitude and speed are judged each on its own stamp. A crossing altitude a CFIX or
    /// constrained direct-to of this same transmission stamped (<see cref="NavigationTarget.CrossingTransmissionId"/>)
    /// stays, and every other altitude a controller's crossing set gets the procedure's published restriction back. The
    /// clearance cancels only the speeds ATC assigned before it (7110.65 5-7-1.d), and a pilot keeps a published speed
    /// (AIM 5-4-1 NOTE), so a fix's speed is reset only when an earlier transmission set it
    /// (<see cref="NavigationTarget.SpeedTransmissionId"/>), when it carries a speed revert, or when the via procedure
    /// publishes the fix; one whose speed this same transmission set keeps it, and every other fix keeps its speed, as an
    /// approach's appended fixes, a STAR's fixes on a climb via route, and every fix of a procedure that cannot be resolved
    /// do. A reset fix takes the pending or active approach clearance's published speed for it, else the via procedure's,
    /// else none. The via procedure's restrictions are <paramref name="publishedRestrictions"/>'s (the active STAR's for a
    /// DVIA, the active SID's for a CVIA).
    /// </summary>
    private static void SupersedeEarlierCrossingRestrictions(
        AircraftState aircraft,
        int transmissionId,
        Func<AircraftState, Dictionary<string, NavigationTarget>> publishedRestrictions
    )
    {
        var published = new Lazy<Dictionary<string, NavigationTarget>>(() => publishedRestrictions(aircraft));
        var approachSpeeds = new Lazy<Dictionary<string, CifpSpeedRestriction>>(() => ClearedApproachSpeeds(aircraft));
        foreach (NavigationTarget fix in EffectiveRoute(aircraft))
        {
            if (AltitudeSetBefore(fix, transmissionId))
            {
                fix.AltitudeRestriction = published.Value.GetValueOrDefault(fix.Name)?.AltitudeRestriction;
                fix.CrossingTransmissionId = null;
            }

            if (ResetsSpeed(fix, transmissionId, published))
            {
                fix.SpeedRestriction =
                    approachSpeeds.Value.GetValueOrDefault(fix.Name) ?? published.Value.GetValueOrDefault(fix.Name)?.SpeedRestriction;
                fix.SpeedTransmissionId = null;
            }

            fix.RevertAltitude = null;
            fix.RevertAssignedAltitude = null;
            ClearSpeedReverts(fix);
        }
    }

    /// <summary>
    /// Whether a controller's crossing set <paramref name="fix"/>'s altitude outside transmission
    /// <paramref name="transmissionId"/>: a crossing stamp of another transmission, or reverts left by a crossing with none.
    /// </summary>
    private static bool AltitudeSetBefore(NavigationTarget fix, int transmissionId) =>
        ((fix.CrossingTransmissionId is not null) || HasPendingRevert(fix)) && !IsStampedBy(fix, transmissionId);

    /// <summary>
    /// Whether a via clearance of transmission <paramref name="transmissionId"/> resets <paramref name="fix"/>'s speed: its
    /// speed is not this transmission's, and an earlier transmission set it, it carries a speed revert, or the via
    /// procedure (<paramref name="published"/>) publishes the fix. Read before the clearance drops the fix's reverts.
    /// </summary>
    private static bool ResetsSpeed(NavigationTarget fix, int transmissionId, Lazy<Dictionary<string, NavigationTarget>> published) =>
        !IsSpeedStampedBy(fix, transmissionId)
        && ((fix.SpeedTransmissionId is not null) || HasSpeedRevert(fix) || published.Value.ContainsKey(fix.Name));

    /// <summary>
    /// A via clearance cancels any speed assigned before it, a superseded crossing speed included, so the procedure's
    /// published speeds and the default schedule take the speed back (7110.65 5-7-1; AIM 4-4-12). A speed assigned in
    /// the via clearance's transmission (or a later one) and accepted before it fires
    /// (<see cref="SpeedStandsUnderClearance"/>) is restated with it (7110.65 5-7-1.c) and stands. The cancel ends only the
    /// ATC assignment: on a STAR the last crossed published speed is the ceiling again
    /// (<see cref="FlightCommandHandler.RestoreCrossedPublishedCeiling"/>), and a crossing speed of this same transmission
    /// stays in force (<see cref="KeepTransmissionCrossingSpeed"/>).
    /// </summary>
    private static void CancelEarlierAssignedSpeed(AircraftState aircraft, int transmissionId)
    {
        if (!SpeedStandsUnderClearance(aircraft, transmissionId))
        {
            FlightCommandHandler.CancelAssignedSpeedKeepingProcedureFloor(aircraft);
            FlightCommandHandler.RestoreCrossedPublishedCeiling(aircraft);
            KeepTransmissionCrossingSpeed(aircraft, EffectiveRoute(aircraft), transmissionId);
        }
    }

    /// <summary>
    /// Whether the speed in force stands under a via or approach clearance of transmission
    /// <paramref name="transmissionId"/> as it fires: it was accepted in that transmission or a later one
    /// (<see cref="ControlTargets.AssignedSpeedTransmissionId"/>), so it is restated with the clearance or issued after it,
    /// not previously assigned (7110.65 5-7-1.c, d; AIM 4-4-12.f.5, g). A speed with no stamp, or one from an earlier
    /// transmission, is cancelled.
    /// </summary>
    internal static bool SpeedStandsUnderClearance(AircraftState aircraft, int transmissionId) =>
        aircraft.Targets.AssignedSpeedTransmissionId is { } accepted && (accepted >= transmissionId);

    /// <summary>
    /// An approach clearance cancels every speed assigned before it (7110.65 5-7-1.d), so no crossing restriction on the
    /// route may restore one past its fix: every fix drops its speed reverts, as a via clearance's
    /// <see cref="SupersedeEarlierCrossingRestrictions"/> drops them. The crossing restrictions stay; an earlier
    /// transmission's crossing speed goes back to the published one in <see cref="RestorePublishedSpeedsOfEarlierCrossings"/>.
    /// </summary>
    internal static void DropSpeedReverts(AircraftState aircraft)
    {
        foreach (NavigationTarget fix in EffectiveRoute(aircraft))
        {
            ClearSpeedReverts(fix);
        }
    }

    /// <summary>Whether <paramref name="fix"/> holds any speed revert: plain (with its bounds), Mach, SMAX's re-arm or a resume.</summary>
    internal static bool HasSpeedRevert(NavigationTarget fix) =>
        HasPlainRevert(fix) || (fix.RevertMach is not null) || fix.RevertSpeedMax || (fix.RevertResume is not null);

    /// <summary>
    /// The fixes among <paramref name="fixes"/> that hold a speed revert, in route order. A route change takes them before
    /// it clears or truncates the route and hands them to <see cref="ApplyTakenSpeedReverts"/> once its new state is in
    /// place.
    /// </summary>
    internal static List<NavigationTarget> TakePendingSpeedReverts(IReadOnlyList<NavigationTarget> fixes) => [.. fixes.Where(HasSpeedRevert)];

    /// <summary>
    /// Applies the speed reverts a route change took from the fixes it dropped (<see cref="TakePendingSpeedReverts"/>), as
    /// if each fix were crossed (<see cref="FlightPhysics.RestoreSpeedPastFix"/>), so the speed a crossing replaced comes
    /// back rather than the crossing speed holding for good (AIM 4-4-10.g; 7110.65 §5-7-1.b-d). Most route changes call it
    /// once their new route and procedure are in place, so an RNS or RPS resumes against them; a constrained direct-to
    /// that replaces the route calls it on the emptied route, before it builds its fixes, and with crossing speeds carries
    /// what it restored past its own last crossing speed (one that keeps the active STAR applies, with the STAR still
    /// active, the reverts of the fixes it drops and of the fixes whose speed it sets). In a hold or inside the final gate
    /// the reverts go instead. An SMAX re-arm is armed now: no crossing on a dropped fix can carry it further.
    /// </summary>
    internal static void ApplyTakenSpeedReverts(AircraftState aircraft, List<NavigationTarget> holders)
    {
        foreach (NavigationTarget holder in holders)
        {
            FlightPhysics.RestoreSpeedPastFix(aircraft, holder, []);
        }
    }

    /// <summary>
    /// Clears the route as a route change does, applying the speed reverts its fixes held once it is empty
    /// (<see cref="ApplyTakenSpeedReverts"/>).
    /// </summary>
    internal static void ClearRouteApplyingSpeedReverts(AircraftState aircraft)
    {
        List<NavigationTarget> holders = TakePendingSpeedReverts(aircraft.Targets.NavigationRoute);
        aircraft.Targets.NavigationRoute.Clear();
        ApplyTakenSpeedReverts(aircraft, holders);
    }

    /// <summary>Clears every speed revert on <paramref name="fix"/>: plain (with its bounds), Mach, SMAX's re-arm and resume.</summary>
    internal static void ClearSpeedReverts(NavigationTarget fix)
    {
        fix.RevertSpeed = null;
        fix.RevertAssignedSpeed = null;
        fix.RevertSpeedFloor = null;
        fix.RevertSpeedCeiling = null;
        fix.RevertMach = null;
        fix.RevertMachExplicit = null;
        fix.RevertSpeedMax = false;
        fix.RevertResume = null;
    }

    /// <summary>
    /// A speed instruction (SPD, SPEEDF, SPDN, MACH, SMAX, RNS, RPS, DSR, RFAS) from a later transmission than a crossing
    /// restriction's cancels the crossing's speed, as an approach clearance does (7110.65 5-7-1; AIM 4-4-12.f.1): each
    /// fix whose speed an earlier transmission set gets its published speed back and loses its speed stamp
    /// (<see cref="RestorePublishedSpeedsOfEarlierCrossings"/>), every speed revert on the route goes
    /// (<see cref="DropSpeedReverts"/>), and the crossing altitudes stay. The aircraft flies the new instruction now, at
    /// the fix and past it. Called once the instruction has set the aircraft's speed state; an RNS, RPS or DSR passes its
    /// <paramref name="resume"/>, and its end state (<see cref="FlightCommandHandler.ApplySpeedResume"/>) is written here,
    /// after the published speeds are back.
    /// <para>A crossing speed the same transmission (<paramref name="transmissionId"/>) set before the instruction
    /// ("CFIX PMD 110 250, SPD 280") stands: the instruction becomes what the aircraft goes back to past the last
    /// controller-set crossing (the resume, SMAX's re-arm, the Mach hold, or the plain speed with its assignment and
    /// bounds), and the first such crossing's speed is the assigned speed again, as
    /// <see cref="KeepTransmissionCrossingSpeed"/> leaves it. A speed bound and a DSR's deletion wait for the fix, so
    /// neither overrides the crossing before it. The target speed is left as the instruction set it: the look-ahead
    /// planner times the slowdown for the crossing under an explicit speed too, and an acceleration toward it applies at
    /// once from the CFIX.</para>
    /// </summary>
    internal static void CancelEarlierCrossingSpeeds(AircraftState aircraft, int transmissionId, SpeedResumeKind? resume)
    {
        RestorePublishedSpeedsOfEarlierCrossings(aircraft, transmissionId);
        DropSpeedReverts(aircraft);
        if (resume is { } resumed)
        {
            FlightCommandHandler.ApplySpeedResume(aircraft, resumed);
        }

        List<NavigationTarget> route = EffectiveRoute(aircraft);
        NavigationTarget? firstCrossing = route.Find(fix => IsSpeedStampedBy(fix, transmissionId) && (fix.SpeedRestriction is not null));
        if (firstCrossing?.SpeedRestriction is not { } crossing)
        {
            return;
        }

        ControlTargets targets = aircraft.Targets;
        PlaceSameTransmissionRevert(aircraft, route, resume);
        FlightCommandHandler.EndMachHoldAndSpeedMax(aircraft);
        targets.AssignedSpeed = crossing.SpeedKts;
    }

    /// <summary>
    /// For a speed instruction given in a crossing's own transmission, puts what it set on the route as the one pending
    /// revert, on the last controller-set crossing: the resume (a DSR's deletion waiting for the fix), else SMAX's
    /// re-arm, else the Mach hold, else the plain speed with its assignment and bounds, whose bounds then wait for the fix.
    /// </summary>
    private static void PlaceSameTransmissionRevert(AircraftState aircraft, List<NavigationTarget> route, SpeedResumeKind? resume)
    {
        ControlTargets targets = aircraft.Targets;
        if (resume is { } kind)
        {
            PlaceResumeRevert(route, kind);
            if (kind == SpeedResumeKind.Deleted)
            {
                // DSR stops the planner and the published speeds; the crossing is flown first and the deletion applies at it.
                aircraft.Procedure.SpeedRestrictionsDeleted = false;
            }
        }
        else if (targets.SpeedMaxHeld)
        {
            PlaceSpeedMaxRevert(route);
        }
        else if (targets.TargetMach is { } mach)
        {
            PlaceMachRevert(route, mach, targets.HasExplicitSpeedCommand);
        }
        else
        {
            PlacePlainRevert(route, targets.TargetSpeed, targets.AssignedSpeed, targets.SpeedFloor, targets.SpeedCeiling);
            targets.SpeedFloor = null;
            targets.SpeedCeiling = null;
        }
    }

    /// <summary>
    /// The plain counterpart of <see cref="PlaceMachRevert"/>: the speed, assignment and bounds a crossing goes back to
    /// (<see cref="NavigationTarget.RevertSpeed"/>, <see cref="NavigationTarget.RevertAssignedSpeed"/>,
    /// <see cref="NavigationTarget.RevertSpeedFloor"/>, <see cref="NavigationTarget.RevertSpeedCeiling"/>), from one
    /// capture, sit on the route-order-last fix whose speed the controller set
    /// (<see cref="FlightPhysics.IsControllerSetSpeed"/>) and on no other, so every crossing speed before it is flown
    /// first. The fix that holds them keeps no resume.
    /// </summary>
    internal static void PlacePlainRevert(List<NavigationTarget> route, double? speed, double? assignedSpeed, double? floor, double? ceiling)
    {
        int last = route.FindLastIndex(FlightPhysics.IsControllerSetSpeed);
        for (int i = 0; i < route.Count; i++)
        {
            bool holds = i == last;
            route[i].RevertSpeed = holds ? speed : null;
            route[i].RevertAssignedSpeed = holds ? assignedSpeed : null;
            route[i].RevertSpeedFloor = holds ? floor : null;
            route[i].RevertSpeedCeiling = holds ? ceiling : null;
        }

        if (last >= 0)
        {
            route[last].RevertResume = null;
        }
    }

    /// <summary>
    /// Puts an RNS, RPS or DSR given in a crossing's own transmission (<see cref="NavigationTarget.RevertResume"/>) on the
    /// route-order-last fix whose speed the controller set, as <see cref="PlacePlainRevert"/> places a speed, and clears it
    /// on every other fix, with any plain revert there, so the route keeps one pending revert. The fix that holds it keeps
    /// no other speed revert.
    /// </summary>
    internal static void PlaceResumeRevert(List<NavigationTarget> route, SpeedResumeKind resume)
    {
        int last = route.FindLastIndex(FlightPhysics.IsControllerSetSpeed);
        foreach (NavigationTarget fix in route)
        {
            fix.RevertResume = null;
            fix.RevertSpeed = null;
            fix.RevertAssignedSpeed = null;
            fix.RevertSpeedFloor = null;
            fix.RevertSpeedCeiling = null;
        }

        if (last >= 0)
        {
            ClearSpeedReverts(route[last]);
            route[last].RevertResume = resume;
        }
    }

    /// <summary>
    /// Whether a crossing speed set now reverts to SMAX past its crossings: SMAX is in force, or a route fix already
    /// arms it again (<see cref="NavigationTarget.RevertSpeedMax"/>). <paramref name="route"/> is the route the crossing goes
    /// on: the effective one for a CFIX (<see cref="EffectiveRoute"/>), the live one for a command that rebuilds it.
    /// </summary>
    internal static bool SpeedMaxPending(AircraftState aircraft, List<NavigationTarget> route) =>
        aircraft.Targets.SpeedMaxHeld || route.Exists(fix => fix.RevertSpeedMax);

    /// <summary>
    /// Puts SMAX's re-arm (<see cref="NavigationTarget.RevertSpeedMax"/>) on the route-order-last fix whose speed the
    /// controller set (<see cref="FlightPhysics.IsControllerSetSpeed"/>) and clears it on every other fix, so every
    /// crossing speed before that fix is flown first. The fix that holds it keeps no plain speed revert. Returns whether
    /// a fix holds it.
    /// </summary>
    internal static bool PlaceSpeedMaxRevert(List<NavigationTarget> route)
    {
        int last = route.FindLastIndex(FlightPhysics.IsControllerSetSpeed);
        for (int i = 0; i < route.Count; i++)
        {
            route[i].RevertSpeedMax = i == last;
        }

        if (last >= 0)
        {
            route[last].RevertSpeed = null;
            route[last].RevertAssignedSpeed = null;
            route[last].RevertSpeedFloor = null;
            route[last].RevertSpeedCeiling = null;
            route[last].RevertMach = null;
            route[last].RevertMachExplicit = null;
            route[last].RevertResume = null;
        }

        return last >= 0;
    }

    /// <summary>
    /// The counterpart of <see cref="PlaceSpeedMaxRevert"/> for <see cref="NavigationTarget.RevertMach"/>: the Mach revert
    /// already pending on the route (<paramref name="mach"/>) moves to the route-order-last fix whose speed the controller
    /// set (<see cref="FlightPhysics.IsControllerSetSpeed"/>), so every crossing speed before it is flown first and the
    /// Mach comes back past the last one. The fix that holds it keeps no plain speed revert.
    /// </summary>
    internal static void PlaceMachRevert(List<NavigationTarget> route, double mach, bool? explicitFlag)
    {
        int last = route.FindLastIndex(FlightPhysics.IsControllerSetSpeed);
        if (last < 0)
        {
            return;
        }

        foreach (NavigationTarget fix in route)
        {
            fix.RevertMach = null;
            fix.RevertMachExplicit = null;
        }

        route[last].RevertMach = mach;
        route[last].RevertMachExplicit = explicitFlag;
        route[last].RevertSpeed = null;
        route[last].RevertAssignedSpeed = null;
        route[last].RevertSpeedFloor = null;
        route[last].RevertSpeedCeiling = null;
        route[last].RevertResume = null;
    }

    /// <summary>
    /// An approach clearance or a later speed instruction (<see cref="CancelEarlierCrossingSpeeds"/>) cancels a crossing
    /// speed issued in an earlier transmission, as either cancels the speed assigned before it (7110.65 5-7-1.d; AIM
    /// 4-4-12.f): each route fix whose speed an earlier transmission set (<see cref="NavigationTarget.SpeedTransmissionId"/>)
    /// gets its published speed back, the pending or active approach clearance's for that fix, else the active STAR's,
    /// else none, as a via clearance's <see cref="SupersedeEarlierCrossingRestrictions"/> does. A speed set by the
    /// cancelling transmission itself (<paramref name="transmissionId"/>) stays. The fix's altitude restriction and
    /// altitude reverts stay; its speed reverts go with <see cref="DropSpeedReverts"/>.
    /// </summary>
    internal static void RestorePublishedSpeedsOfEarlierCrossings(AircraftState aircraft, int transmissionId)
    {
        Dictionary<string, CifpSpeedRestriction>? approachSpeeds = null;
        Dictionary<string, NavigationTarget>? starRestrictions = null;
        foreach (NavigationTarget fix in EffectiveRoute(aircraft))
        {
            if ((fix.SpeedTransmissionId is null) || IsSpeedStampedBy(fix, transmissionId))
            {
                continue;
            }

            approachSpeeds ??= ClearedApproachSpeeds(aircraft);
            starRestrictions ??= StarRestrictionsForSpeedRestore(aircraft);
            fix.SpeedRestriction = approachSpeeds.GetValueOrDefault(fix.Name) ?? starRestrictions.GetValueOrDefault(fix.Name)?.SpeedRestriction;
            fix.SpeedTransmissionId = null;
        }
    }

    /// <summary>
    /// The speeds the approach just cleared publishes, by fix name: the pending clearance's, else the active one's, read
    /// from the transition it cleared (<see cref="ApproachClearance.TransitionName"/>) and then its common legs, the first
    /// leg to give a fix a speed winning; a clearance with no transition, or one its procedure does not name, reads the
    /// common legs only. Each keeps its CIFP type, as the approach's fixes enter the route
    /// (<c>ApproachCommandHandler.AppendApproachFixesToNavRoute</c>). Empty for a clearance with no procedure.
    /// </summary>
    internal static Dictionary<string, CifpSpeedRestriction> ClearedApproachSpeeds(AircraftState aircraft)
    {
        var byName = new Dictionary<string, CifpSpeedRestriction>(StringComparer.OrdinalIgnoreCase);
        ApproachClearance? clearance = aircraft.Approach.PendingClearance?.Clearance ?? aircraft.Phases?.ActiveApproach;
        if (clearance?.Procedure is not { } procedure)
        {
            return byName;
        }

        foreach (CifpLeg leg in ClearedTransitionLegs(clearance, procedure).Concat(procedure.CommonLegs))
        {
            if ((leg.Speed is { } speed) && !string.IsNullOrEmpty(leg.FixIdentifier))
            {
                byName.TryAdd(leg.FixIdentifier, speed);
            }
        }

        return byName;
    }

    /// <summary>
    /// The legs of the transition <paramref name="clearance"/> cleared (<see cref="ApproachClearance.TransitionName"/>) on
    /// <paramref name="procedure"/>; none for a clearance with no transition, or one the procedure does not name.
    /// </summary>
    private static IReadOnlyList<CifpLeg> ClearedTransitionLegs(ApproachClearance clearance, CifpApproachProcedure procedure) =>
        ((clearance.TransitionName is { } name) && (procedure.Transitions.TryGetValue(name, out CifpTransition? transition))) ? transition.Legs : [];

    /// <summary>
    /// The active STAR's published restrictions by fix name, for <see cref="RestorePublishedSpeedsOfEarlierCrossings"/>;
    /// empty, and without a warning, when no STAR is active (a vectored aircraft, or one an approach clearance took off its
    /// STAR).
    /// </summary>
    private static Dictionary<string, NavigationTarget> StarRestrictionsForSpeedRestore(AircraftState aircraft)
    {
        if (aircraft.Procedure.ActiveStarId is not { } starId)
        {
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, starId);
        if (star is null)
        {
            Log.LogWarning(
                "{Callsign}: could not resolve STAR {Star} at {Dest}; cancelled earlier crossing speeds are cleared with no published speed",
                aircraft.Callsign,
                starId,
                aircraft.FlightPlan.Destination
            );
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        return PublishedStarRestrictions(star, aircraft);
    }

    /// <summary>
    /// After a via or approach clearance has cancelled the speed assigned before it, keeps in force a crossing speed a
    /// CFIX or constrained direct-to of the clearance's own transmission (<paramref name="transmissionId"/>) set, as its
    /// speed stamp records: the crossing was restated with the clearance (7110.65 5-7-1.c), the speed it would revert to
    /// past its fix was not. Each such fix drops its speed reverts, and the first one's speed is the assigned speed again for
    /// the data block, as when the crossing is issued after the clearance. The crossing itself stays on its fix, which the
    /// speed stamp keeps. An altitude-only CFIX restates no speed, so its fix's published speed is never assigned here.
    /// </summary>
    internal static void KeepTransmissionCrossingSpeed(AircraftState aircraft, List<NavigationTarget> route, int transmissionId)
    {
        NavigationTarget? first = null;
        foreach (NavigationTarget fix in route)
        {
            if (!IsSpeedStampedBy(fix, transmissionId) || (fix.SpeedRestriction is null))
            {
                continue;
            }

            ClearSpeedReverts(fix);
            first ??= fix;
        }

        if (first?.SpeedRestriction is { } crossing)
        {
            aircraft.Targets.AssignedSpeed = crossing.SpeedKts;
        }
    }

    private static bool IsStampedBy(NavigationTarget fix, int transmissionId) =>
        (transmissionId != 0) && (fix.CrossingTransmissionId == transmissionId);

    private static bool IsSpeedStampedBy(NavigationTarget fix, int transmissionId) =>
        (transmissionId != 0) && (fix.SpeedTransmissionId == transmissionId);

    private static bool HasPendingRevert(NavigationTarget fix) =>
        (fix.RevertAltitude is not null) || (fix.RevertAssignedAltitude is not null) || HasSpeedRevert(fix);

    /// <summary>
    /// The speed the procedure of the via clearance in force publishes at <paramref name="fixName"/>, whatever a controller
    /// set there; null when it publishes none, its procedure cannot be resolved or "delete speed restrictions" stands. The
    /// fix must still be on the route: an enroute transition counts as flown only while one of its own fixes is.
    /// </summary>
    internal static CifpSpeedRestriction? ViaProcedureSpeedAt(AircraftState aircraft, string fixName)
    {
        if (aircraft.Procedure.SpeedRestrictionsDeleted)
        {
            return null;
        }

        if (ViaProcedureRestrictions(aircraft) is not { } published)
        {
            Log.LogWarning(
                "{Callsign}: could not resolve the via clearance's procedure (STAR {Star}, SID {Sid}); no published speed read at {Fix}",
                aircraft.Callsign,
                aircraft.Procedure.ActiveStarId,
                aircraft.Procedure.ActiveSidId,
                fixName
            );
            return null;
        }

        return published.GetValueOrDefault(fixName)?.SpeedRestriction;
    }

    /// <summary>The published restrictions of the via clearance's procedure by fix name; null when it cannot be resolved.</summary>
    private static Dictionary<string, NavigationTarget>? ViaProcedureRestrictions(AircraftState aircraft)
    {
        if (aircraft.Procedure.StarViaMode)
        {
            return ResolveActiveStar(aircraft) is { } star ? PublishedStarRestrictions(star, aircraft) : null;
        }

        return ResolveActiveSid(aircraft) is { } sid ? PublishedSidRestrictions(sid, aircraft) : null;
    }

    private static CifpStarProcedure? ResolveActiveStar(AircraftState aircraft)
    {
        string? starId = aircraft.Procedure.ActiveStarId;
        return starId is null ? null : NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, starId);
    }

    /// <summary>The active STAR's published restrictions by fix name; empty when the STAR cannot be resolved.</summary>
    private static Dictionary<string, NavigationTarget> ActiveStarRestrictions(AircraftState aircraft)
    {
        if (ResolveActiveStar(aircraft) is not { } star)
        {
            Log.LogDebug(
                "{Callsign}: STAR {Star} at {Dest} unresolved; superseded crossing restrictions are cleared, not restored",
                aircraft.Callsign,
                aircraft.Procedure.ActiveStarId,
                aircraft.FlightPlan.Destination
            );
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        return PublishedStarRestrictions(star, aircraft);
    }

    /// <summary>
    /// The restrictions <paramref name="star"/> publishes on the legs <paramref name="aircraft"/> flies, by fix name,
    /// in the order <c>JARR</c> builds them: the enroute transition(s) its route is on, the common route, then the
    /// runway transition for its runway (the assigned runway, else the procedure's destination runway). Where no
    /// runway transition resolves for it, every runway transition is read, the first to name a fix winning.
    /// </summary>
    private static Dictionary<string, NavigationTarget> PublishedStarRestrictions(CifpStarProcedure star, AircraftState aircraft)
    {
        var commonFixes = new HashSet<string>(star.CommonLegs.Select(l => l.FixIdentifier), StringComparer.OrdinalIgnoreCase);
        var routeFixes = new HashSet<string>(aircraft.Targets.NavigationRoute.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var orderedLegs = new List<CifpLeg>();
        foreach (CifpTransition transition in star.EnrouteTransitions.Values)
        {
            if (transition.Legs.Any(l => routeFixes.Contains(l.FixIdentifier) && !commonFixes.Contains(l.FixIdentifier)))
            {
                orderedLegs.AddRange(transition.Legs);
            }
        }

        orderedLegs.AddRange(star.CommonLegs);

        string? runway = aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Procedure.DestinationRunway;
        CifpTransition? flownRunwayTransition = string.IsNullOrEmpty(runway) ? null : LookupRunwayTransition(star.RunwayTransitions, runway);
        if (flownRunwayTransition is not null)
        {
            orderedLegs.AddRange(flownRunwayTransition.Legs);
        }
        else
        {
            foreach (CifpTransition transition in star.RunwayTransitions.Values)
            {
                orderedLegs.AddRange(transition.Legs);
            }
        }

        var byName = new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (NavigationTarget target in DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs))
        {
            if (((target.AltitudeRestriction is not null) || (target.SpeedRestriction is not null)) && !byName.ContainsKey(target.Name))
            {
                byName[target.Name] = target;
            }
        }

        return byName;
    }

    /// <summary>
    /// Activates the SID named in the aircraft's filed route when none is active yet, so a bare
    /// <c>CVIA</c> works after the aircraft was given runway heading (<c>CTO RH</c>) or vectored off
    /// its SID and then re-cleared <c>DCT</c> to a downstream SID fix (the SID being in the cleared
    /// route is the only real-world precondition for "climb via", AIM 5-2-9). Sets
    /// <see cref="AircraftProcedure.ActiveSidId"/> and overlays the published CIFP altitude/speed
    /// restrictions onto the existing (already-sequenced) lateral route by fix name — it does not
    /// rebuild the route, so fixes the aircraft has already passed are not re-added. A fix keeps the crossing altitude
    /// or speed a CFIX in transmission <paramref name="transmissionId"/> set (0 names no transmission). Returns false
    /// when the aircraft is VFR or the filed route contains no resolvable SID.
    /// </summary>
    internal static bool TryActivateFiledSid(AircraftState aircraft, int transmissionId)
    {
        // SIDs are an IFR construct; a VFR aircraft never flies one.
        if (aircraft.FlightPlan.IsVfr)
        {
            return false;
        }

        if (FiledProcedureLookup.FindSid(NavigationDatabase.Instance, aircraft.FlightPlan.Departure, aircraft.FlightPlan.Route) is not { } filed)
        {
            return false;
        }

        OverlaySidRestrictions(aircraft, filed.Procedure, transmissionId);
        aircraft.Procedure.ActiveSidId = filed.Id;
        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
        return true;
    }

    /// <summary>
    /// Copies each published altitude/speed restriction from <paramref name="sid"/> onto the matching
    /// fix (by name) already present in the aircraft's navigation route, reusing the same leg→target
    /// resolution as the departure clearance so the constraint semantics are identical
    /// (<see cref="OverlayPublishedRestrictions"/>).
    /// </summary>
    private static void OverlaySidRestrictions(AircraftState aircraft, CifpSidProcedure sid, int transmissionId) =>
        OverlayPublishedRestrictions(EffectiveRoute(aircraft), PublishedSidRestrictions(sid, aircraft), transmissionId);

    /// <summary>The active SID's published restrictions by fix name; empty when the SID cannot be resolved.</summary>
    private static Dictionary<string, NavigationTarget> ActiveSidRestrictions(AircraftState aircraft)
    {
        if (ResolveActiveSid(aircraft) is not { } sid)
        {
            Log.LogWarning(
                "{Callsign}: could not resolve SID {Sid} at {Dep}; superseded crossing restrictions are cleared, not restored",
                aircraft.Callsign,
                aircraft.Procedure.ActiveSidId,
                aircraft.FlightPlan.Departure
            );
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        return PublishedSidRestrictions(sid, aircraft);
    }

    /// <summary>
    /// The restrictions <paramref name="sid"/> publishes on the legs <paramref name="aircraft"/> flies, by fix name, first
    /// leg winning.
    /// </summary>
    private static Dictionary<string, NavigationTarget> PublishedSidRestrictions(CifpSidProcedure sid, AircraftState aircraft)
    {
        var byName = new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (NavigationTarget target in DepartureClearanceHandler.ResolveLegsToTargets(FlownSidLegs(sid, aircraft)))
        {
            if (((target.AltitudeRestriction is not null) || (target.SpeedRestriction is not null)) && !byName.ContainsKey(target.Name))
            {
                byName[target.Name] = target;
            }
        }

        return byName;
    }

    /// <summary>
    /// The legs of <paramref name="sid"/> that <paramref name="aircraft"/> flies, in order: the runway transition matching
    /// <see cref="AircraftProcedure.DepartureRunway"/> (a persistent field that survives a phase teardown), so the
    /// first-wins dedup prefers the flown runway's values; the common route; then the enroute transition(s) flown — named
    /// in the filed route, or holding a fix of the live route outside the common route. Where no runway transition can be
    /// identified, every one is read. Where no enroute transition can be identified, none is read: a transition the
    /// aircraft is not known to fly must not raise a climb via's top altitude, nor lay its restrictions on the route.
    /// </summary>
    private static List<CifpLeg> FlownSidLegs(CifpSidProcedure sid, AircraftState aircraft)
    {
        var allLegs = new List<CifpLeg>();

        CifpTransition? assignedRwTransition = FlownRunwayTransition(sid, aircraft);
        if (assignedRwTransition is not null)
        {
            allLegs.AddRange(assignedRwTransition.Legs);
        }
        else
        {
            foreach (CifpTransition transition in sid.RunwayTransitions.Values)
            {
                allLegs.AddRange(transition.Legs);
            }
        }

        allLegs.AddRange(sid.CommonLegs);
        allLegs.AddRange(FlownSidEnrouteTransitions(sid, aircraft).SelectMany(t => t.Legs));
        return allLegs;
    }

    /// <summary>
    /// The runway transition of <paramref name="sid"/> for <see cref="AircraftProcedure.DepartureRunway"/>, the CIFP "B"
    /// suffix standing for both L/R (e.g. "RW28B"); null when the runway is unknown or has no transition.
    /// </summary>
    private static CifpTransition? FlownRunwayTransition(CifpSidProcedure sid, AircraftState aircraft)
    {
        if (aircraft.Procedure.DepartureRunway is not { } rwy)
        {
            return null;
        }

        if (sid.RunwayTransitions.TryGetValue("RW" + rwy, out CifpTransition? transition))
        {
            return transition;
        }

        return sid.RunwayTransitions.TryGetValue("RW" + rwy.TrimEnd('L', 'R', 'C') + "B", out transition) ? transition : null;
    }

    private static List<CifpTransition> FlownSidEnrouteTransitions(CifpSidProcedure sid, AircraftState aircraft)
    {
        var filedTokens = new HashSet<string>(FiledRouteTokens(aircraft), StringComparer.OrdinalIgnoreCase);
        var commonFixes = new HashSet<string>(sid.CommonLegs.Select(l => l.FixIdentifier), StringComparer.OrdinalIgnoreCase);
        var routeFixes = new HashSet<string>(EffectiveRoute(aircraft).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        List<CifpTransition> flown =
        [
            .. sid.EnrouteTransitions.Values.Where(t =>
                filedTokens.Contains(t.Name) || t.Legs.Any(l => routeFixes.Contains(l.FixIdentifier) && (!commonFixes.Contains(l.FixIdentifier)))
            ),
        ];
        return flown;
    }

    internal static CommandResult DispatchReportFieldInSight(AircraftState aircraft, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return CommandDispatcher.OperatorGuidanceRefusal("Use RFIS <clock> <miles> in solo training");
        }

        return DispatchReportFieldInSightCore(aircraft, ctx);
    }

    private static CommandResult DispatchReportFieldInSightCore(AircraftState aircraft, DispatchContext ctx)
    {
        // Fast path: if the tick processor has already confirmed acquisition on a
        // visual approach, just echo the in-sight response.
        if (aircraft.Approach.HasReportedFieldInSight)
        {
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildFieldInSight(aircraft)
            );
            return CommandDispatcher.Ok("Field in sight");
        }

        string destination = aircraft.FlightPlan.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return new CommandResult(false, "Unable, no arrival airport assigned");
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (navDb.GetAirportPosition(destination) is null || navDb.GetAirportElevation(destination) is null)
        {
            return new CommandResult(false, $"Unable, {destination} not in nav database");
        }

        MetarParser.ParsedMetar? metar = ctx.Weather?.GetWeatherForAirport(destination);
        VisualAcquisitionResult result =
            VisualAcquisition.TryAcquireAirport(aircraft, ctx.Weather)
            ?? throw new InvalidOperationException($"Destination {destination} pre-validated but TryAcquireAirport returned null");

        if (result.Acquired)
        {
            // Setting the flag here unblocks the CVA FOLLOW gate and lets the tick
            // processor take over maintained-contact tracking once visual clearance
            // becomes active. First-check acquisition supersedes any in-flight
            // "looking" state.
            aircraft.Approach.HasReportedFieldInSight = true;
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildFieldInSight(aircraft)
            );
            aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
            return CommandDispatcher.Ok("Field in sight");
        }

        // Soft-fail: pilot acknowledges the request but can't see the field yet.
        // Record the reason in a pilot readback and keep looking each tick via
        // PilotObservationUpdater. A new RFIS replaces the prior observation —
        // the latest request always wins.
        aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
        aircraft.PendingObservations.Add(new FieldAcquisitionObservation());
        if (ctx.SoloTrainingMode)
        {
            Pilot.PilotResponder.QueueSoloPilotReadback(
                aircraft,
                FormatFieldLookingNotification(result, destination),
                Pilot.PilotResponder.SourceSayReadback
            );
        }
        else
        {
            aircraft.PendingPilotReadbacks.Add(FormatFieldLookingNotification(result, destination));
        }
        return CommandDispatcher.Ok($"Looking for the field — {FormatFieldFailureHint(result, metar, destination)}");
    }

    internal static CommandResult DispatchReportFieldAdvisory(ReportFieldAdvisoryCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        CommandResult acquisition = DispatchReportFieldInSightCore(aircraft, ctx);
        return acquisition.Success ? CommandDispatcher.Ok(CommandDescriber.FormatFieldAdvisoryPhrase(cmd.Details)) : acquisition;
    }

    internal static CommandResult DispatchReportTrafficInSight(AircraftState aircraft, string? targetCallsign, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return CommandDispatcher.OperatorGuidanceRefusal("Use RTIS <clock> <miles> <direction> <type> [altitude] in solo training");
        }

        string? normalizedCallsign = string.IsNullOrWhiteSpace(targetCallsign) ? null : targetCallsign.Trim().ToUpperInvariant();

        // Fast path: the aircraft already has traffic in sight (tick processor confirmed an active
        // FOLLOW, or a prior RTIS/RTISF/FOLLOWF). A bare RTIS — or one naming the same traffic
        // already reported — re-echoes the in-sight response without re-running visual acquisition
        // (which could now geometrically soft-fail on the followed traffic). A different callsign is
        // a new sighting: fall through to full validate + acquisition so a bogus callsign is
        // rejected and a real one is actually acquired.
        if (
            aircraft.Approach.HasReportedTrafficInSight
            && ((normalizedCallsign is null) || (normalizedCallsign == aircraft.Approach.LastReportedTrafficCallsign))
        )
        {
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildTrafficInSight(aircraft, normalizedCallsign)
            );
            return CommandDispatcher.Ok("Traffic in sight");
        }

        if (normalizedCallsign is null)
        {
            return new CommandResult(false, "Unable, no traffic specified");
        }

        AircraftState? target = ctx.FindAircraft?.Invoke(normalizedCallsign);
        if (target is null)
        {
            return new CommandResult(false, $"Negative contact, {normalizedCallsign} not on this frequency");
        }

        return DispatchReportTrafficInSightForTarget(aircraft, normalizedCallsign, target, ctx);
    }

    private static CommandResult DispatchReportTrafficInSightForTarget(
        AircraftState aircraft,
        string targetCallsign,
        AircraftState target,
        DispatchContext ctx
    )
    {
        VisualAcquisitionResult result = VisualAcquisition.TryAcquireTraffic(aircraft, target, ctx.Weather);

        if (result.Acquired)
        {
            aircraft.Approach.HasReportedTrafficInSight = true;
            aircraft.Approach.LastReportedTrafficCallsign = targetCallsign.ToUpperInvariant();
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildTrafficInSight(aircraft, targetCallsign)
            );
            // First-check acquisition supersedes any in-flight "looking" state.
            aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
            return CommandDispatcher.Ok("Traffic in sight");
        }

        // Soft-fail: pilot acknowledges the request but can't see the traffic yet.
        // Record the reason in a pilot readback and keep looking each tick via
        // PilotObservationUpdater. A new RTIS (same or different callsign) replaces
        // the prior observation — the latest request always wins.
        string targetCallsignUpper = targetCallsign.ToUpperInvariant();
        aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
        aircraft.PendingObservations.Add(
            new TrafficAcquisitionObservation(targetCallsignUpper, ctx.ScenarioElapsedSeconds, ArmedAirborne: !aircraft.IsOnGround)
        );
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildTrafficNegativeContactLooking(aircraft, target.Callsign, result.Reason)
        );
        return CommandDispatcher.Ok($"Looking for traffic — {FormatTrafficFailureHint(result, target)}");
    }

    internal static CommandResult DispatchReportTrafficAdvisory(ReportTrafficAdvisoryCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficAdvisoryPhrase(cmd.Details));
    }

    /// <summary>
    /// Runs the in-sight report against the aircraft an advisory resolved to (looked up by callsign in the snapshot the
    /// candidates were built from) and answers with <paramref name="phrase"/> when the report is accepted.
    /// </summary>
    private static CommandResult AcknowledgeResolvedAdvisory(
        AircraftState aircraft,
        TrafficAdvisoryTargetMatch match,
        IReadOnlyList<AircraftState>? snapshot,
        DispatchContext ctx,
        string phrase
    )
    {
        AircraftState target =
            snapshot?.FirstOrDefault(candidate => candidate.Callsign.Equals(match.Target.Callsign, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Resolved traffic {match.Target.Callsign} is not in the aircraft list it was resolved from");
        CommandResult acquisition = DispatchReportTrafficInSightForTarget(aircraft, target.Callsign, target, ctx);
        return acquisition.Success ? CommandDispatcher.Ok(phrase) : acquisition;
    }

    internal static CommandResult DispatchReportTrafficRelative(ReportTrafficRelativeCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolveRelativeTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficRelativePhrase(cmd.Details));
    }

    internal static CommandResult DispatchReportTrafficPattern(ReportTrafficPatternCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficPatternPhrase(cmd.Details));
    }

    internal static CommandResult DispatchReportTrafficLandmark(ReportTrafficLandmarkCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        (double Lat, double Lon)? position = NavigationDatabase.Instance.GetFixPosition(cmd.Details.FixName);
        if (position is null)
        {
            return new CommandResult(false, $"Unable, unknown landmark {cmd.Details.FixName}");
        }

        var landmark = new LatLon(position.Value.Lat, position.Value.Lon);
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolveLandmarkTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            landmark,
            cmd.Details.AircraftType,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficLandmarkPhrase(cmd.Details));
    }

    internal static CommandResult DispatchSafetyAlert(SafetyAlertCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        TrafficAdvisoryCandidate? target = TrafficAdvisoryMatcher.ResolveSafetyAlertTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(ctx.ListAircraft?.Invoke()),
            out string error
        );
        if (target is null)
        {
            return new CommandResult(false, error);
        }

        return CommandDispatcher.Ok(CommandDescriber.FormatSafetyAlertPhrase(cmd.Details));
    }

    /// <summary>
    /// RPO-facing diagnostic naming the specific reason the pilot could not
    /// visually acquire the target. Surfaced through the command result so the
    /// instructor/RPO can decide whether to relay it to the student. Distinct
    /// from <see cref="Pilot.PilotResponder.BuildTrafficNegativeContactLooking"/>, which stays in
    /// pilot phraseology and must avoid sim-internal diagnostics.
    /// </summary>
    private static string FormatTrafficFailureHint(VisualAcquisitionResult r, AircraftState target) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.MixedCeiling => $"cloud layer {FormatLayer(r.BindingLayer)} between aircraft",
            VisualAcquisitionFailure.OccludedByBank => "target on high-wing side during bank (occluded)",
            VisualAcquisitionFailure.BehindOwnship => "target behind ownship (outside forward hemisphere)",
            VisualAcquisitionFailure.OutOfRange => $"target {r.DistanceNm:F1} nm away, max visual {r.MaxRangeNm:F1} nm ({target.AircraftType})",
            _ => $"not acquired ({r.Reason})",
        };

    /// <summary>
    /// Pilot readback when RFIS can't be satisfied on the first check — the
    /// pilot acknowledges the request and commits to keep looking. Mirrors
    /// the RTIS soft-fail readback (see <see cref="Pilot.PilotResponder.BuildTrafficNegativeContactLooking"/>):
    /// pilot phraseology only, no simulator-internal diagnostics. "Unable" is
    /// reserved for refused clearances (7110.65 2-4-20) and is NOT used here.
    /// Reviewed with aviation-sim-expert:
    /// - "On top" only fits <see cref="VisualAcquisitionFailure.AboveCeiling"/>
    ///   (the AIM 4-4-8 / 5-5-3 sense of "above an obscuring layer"). For
    ///   <see cref="VisualAcquisitionFailure.InClassA"/> the issue is altitude,
    ///   not a deck, so the readback collapses to the default — the controller
    ///   already knows why at FL180+.
    /// - "Field's behind us" is a real-world idiom for
    ///   <see cref="VisualAcquisitionFailure.BehindOwnship"/> and is actionable
    ///   for the controller (cue to offer a vector).
    /// - <see cref="VisualAcquisitionFailure.OppositeSideOfRunway"/> stays in
    ///   the default arm; runway side isn't standard pilot phraseology in this
    ///   reply.
    /// See AIM 5-4-23 (visual approach) and AIM 4-1-15 / 5-5-8 ("negative
    /// contact" usage).
    /// </summary>
    private static string FormatFieldLookingNotification(VisualAcquisitionResult r, string airportId) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.OccludedByBank => $"Negative contact, {airportId}, in the turn, looking",
            VisualAcquisitionFailure.AboveCeiling => $"Negative contact, {airportId}, on top, looking",
            VisualAcquisitionFailure.BehindOwnship => $"Negative contact, {airportId}, field's behind us, looking",
            _ => $"Negative contact, {airportId}, looking",
        };

    // Phraseology borrowed from AIM §4-1-15 and §5-5-8 (traffic advisories use
    // "negative contact" when the pilot cannot visually acquire a target) and
    // 7110.65 §7-2-1 (visual separation boundary at FL180).

    /// <summary>
    /// RPO-facing diagnostic naming the specific reason the pilot could not
    /// visually acquire the field. Surfaced through the command result so the
    /// instructor/RPO can decide whether to relay it to the student. Distinct
    /// from <see cref="FormatFieldLookingNotification"/>, which stays in
    /// pilot phraseology and must avoid sim-internal diagnostics.
    /// </summary>
    private static string FormatFieldFailureHint(VisualAcquisitionResult r, MetarParser.ParsedMetar? metar, string airportId) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.InClassA => "ownship in Class Alpha",
            VisualAcquisitionFailure.AboveCeiling => $"{airportId} below {FormatLayer(r.BindingLayer)} (cloud deck blocking ground view)",
            VisualAcquisitionFailure.BehindOwnship => $"{airportId} behind ownship (outside forward hemisphere)",
            VisualAcquisitionFailure.OccludedByBank => $"{airportId} on high-wing side during bank (occluded)",
            VisualAcquisitionFailure.OutOfRange =>
                $"{airportId} {r.DistanceNm:F1} nm away, max visual {r.MaxRangeNm:F1} nm{VisibilityQualifier(metar)}",
            VisualAcquisitionFailure.OppositeSideOfRunway => $"{airportId} on opposite side of runway",
            _ => $"not acquired ({r.Reason})",
        };

    /// <summary>
    /// Formats a cloud layer as the 6-character METAR-style code controllers and
    /// pilots actually read out loud, e.g. BKN070 or OVC200. Falls back to a
    /// generic "cloud layer" if no binding layer was recorded on the result.
    /// </summary>
    private static string FormatLayer(MetarParser.CloudLayer? layer)
    {
        if (layer is null)
        {
            return "cloud layer";
        }
        string prefix = layer.Cover switch
        {
            MetarParser.CloudCover.Few => "FEW",
            MetarParser.CloudCover.Scattered => "SCT",
            MetarParser.CloudCover.Broken => "BKN",
            MetarParser.CloudCover.Overcast => "OVC",
            _ => "CLD",
        };
        int hundreds = layer.BaseFeetAgl / 100;
        return $"{prefix}{hundreds:D3}";
    }

    /// <summary>
    /// Clarifies the max-range qualifier for field acquisition. Reported
    /// visibility only participates in the range cap when below the 10SM
    /// reporting maximum (see <see cref="MetarParser.UnrestrictedVisibilitySm"/>),
    /// so only mention it then — a clear-day "10SM" is not a constraint.
    /// </summary>
    private static string VisibilityQualifier(MetarParser.ParsedMetar? metar)
    {
        if (metar?.VisibilityStatuteMiles is double visSm && visSm < MetarParser.UnrestrictedVisibilitySm)
        {
            return $" — visibility {visSm:F0} SM";
        }
        return string.Empty;
    }

    internal static CommandResult DispatchReportFieldInSightForced(AircraftState aircraft, DispatchContext ctx)
    {
        if (ctx.RefusesRpoOnly)
        {
            return CommandDispatcher.OperatorGuidanceRefusal("RFISF is RPO-only; use RFIS <clock> <miles> in solo training");
        }

        aircraft.Approach.HasReportedFieldInSight = true;
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildFieldInSight(aircraft)
        );
        aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
        return CommandDispatcher.Ok("Field in sight (forced)");
    }

    internal static CommandResult DispatchReportTrafficInSightForced(AircraftState aircraft, string? targetCallsign, DispatchContext ctx)
    {
        if (ctx.RefusesRpoOnly)
        {
            return CommandDispatcher.OperatorGuidanceRefusal(
                "RTISF is RPO-only; use RTIS <clock> <miles> <direction> <type> <altitude> in solo training"
            );
        }

        aircraft.Approach.HasReportedTrafficInSight = true;
        if (!string.IsNullOrWhiteSpace(targetCallsign))
        {
            aircraft.Approach.LastReportedTrafficCallsign = targetCallsign.ToUpperInvariant();
        }
        else if (aircraft.PendingObservations.OfType<TrafficAcquisitionObservation>().FirstOrDefault() is { } pending)
        {
            // Bare RTISF folds in a still-pending RTIS target (same rationale as bare FOLLOWF):
            // the target callsign lives only in PendingObservations until acquisition succeeds.
            // RTISF supersedes that look-for-traffic, so consume and clear the observation.
            aircraft.Approach.LastReportedTrafficCallsign = pending.TargetCallsign.ToUpperInvariant();
            aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
        }
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildTrafficInSight(aircraft, targetCallsign)
        );
        return CommandDispatcher.Ok("Traffic in sight (forced)");
    }

    /// <summary>
    /// Arms (or cancels) a deferred pilot position report. The flags live on
    /// <see cref="AircraftApproachState"/> so pattern-leg reports survive lap rebuilds (re-arm each
    /// circuit); N-mile-final and at-fix arms are one-shot and cleared by the tick poll when they
    /// fire. The pilot wilco acknowledgment is produced by the generic solo readback
    /// (<c>PilotResponder.BuildReadback</c>); this handler only records intent and returns the
    /// controller-facing echo. Dry-run safe — touches no <c>TerminalEmitter</c>.
    /// </summary>
    internal static CommandResult DispatchReport(ReportCommand cmd, AircraftState aircraft)
    {
        AircraftApproachState approach = aircraft.Approach;
        switch (cmd.Trigger)
        {
            case ReportTrigger.Cancel:
                return DispatchReportCancel(cmd, aircraft);

            case ReportTrigger.Crosswind:
            case ReportTrigger.Downwind:
            case ReportTrigger.Base:
            case ReportTrigger.Final:
            case ReportTrigger.Midfield:
                if (!HasPatternCircuit(aircraft))
                {
                    return new CommandResult(false, $"Unable, {aircraft.Callsign} is not in the pattern");
                }
                SetPatternLegReportArmed(approach, cmd.Trigger, true);
                return CommandDispatcher.Ok(PatternReportArmedEcho(cmd.Trigger));

            case ReportTrigger.MileFinal:
                if (aircraft.Phases?.AssignedRunway is null)
                {
                    return new CommandResult(false, "Unable, no runway assigned for final reporting");
                }
                if (cmd.DistanceNm is not > 0)
                {
                    return new CommandResult(false, "Final distance must be a positive whole number of miles");
                }
                approach.ReportFinalMileTarget = cmd.DistanceNm;
                return CommandDispatcher.Ok($"Will report {cmd.DistanceNm}-mile final");

            case ReportTrigger.AtFix:
                return DispatchReportAtFix(cmd, aircraft);

            default:
                return new CommandResult(false, "Unrecognized REPORT request");
        }
    }

    private static CommandResult DispatchReportCancel(ReportCommand cmd, AircraftState aircraft)
    {
        switch (cmd.CancelTarget)
        {
            case ReportTrigger.MileFinal:
                return CancelMileFinalReport(aircraft);

            case ReportTrigger.AtFix:
                return CancelAtFixReport(cmd, aircraft);

            case ReportTrigger.Crosswind:
            case ReportTrigger.Downwind:
            case ReportTrigger.Base:
            case ReportTrigger.Final:
            case ReportTrigger.Midfield:
                return CancelPatternLegReport(aircraft.Approach, cmd.CancelTarget.Value);

            default:
                aircraft.Approach.ClearArmedReports();
                return CommandDispatcher.Ok("Reports cancelled");
        }
    }

    private static CommandResult CancelMileFinalReport(AircraftState aircraft)
    {
        if (aircraft.Approach.ReportFinalMileTarget is not { } miles)
        {
            return new CommandResult(false, $"Unable, {aircraft.Callsign} is not reporting a mile final");
        }

        aircraft.Approach.ReportFinalMileTarget = null;
        return CommandDispatcher.Ok($"Cancelled {miles}-mile final report");
    }

    private static CommandResult CancelAtFixReport(ReportCommand cmd, AircraftState aircraft)
    {
        string? fixName = NormalizeFixName(cmd.FixName);
        if (fixName is null)
        {
            return new CommandResult(false, "REPORT requires a fix name");
        }

        if (!string.Equals(aircraft.Approach.ReportAtFixName, fixName, StringComparison.Ordinal))
        {
            return new CommandResult(false, $"Unable, {aircraft.Callsign} is not reporting passing {fixName}");
        }

        aircraft.Approach.ReportAtFixName = null;
        aircraft.Approach.ReportAtFixLat = null;
        aircraft.Approach.ReportAtFixLon = null;
        return CommandDispatcher.Ok($"Cancelled report passing {fixName}");
    }

    private static CommandResult CancelPatternLegReport(AircraftApproachState approach, ReportTrigger leg)
    {
        SetPatternLegReportArmed(approach, leg, false);
        return CommandDispatcher.Ok(PatternReportCancelledEcho(leg));
    }

    /// <summary>The trimmed, upper-cased fix name, or null when the command carried none.</summary>
    private static string? NormalizeFixName(string? fixName)
    {
        string? normalized = fixName?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static CommandResult DispatchReportAtFix(ReportCommand cmd, AircraftState aircraft)
    {
        string? fixName = NormalizeFixName(cmd.FixName);
        if (fixName is null)
        {
            return new CommandResult(false, "REPORT requires a fix name");
        }

        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetFixPosition(fixName);
        if (pos is null)
        {
            return new CommandResult(false, $"Unable, {fixName} not in nav database");
        }

        aircraft.Approach.ReportAtFixName = fixName;
        aircraft.Approach.ReportAtFixLat = pos.Value.Lat;
        aircraft.Approach.ReportAtFixLon = pos.Value.Lon;
        return CommandDispatcher.Ok($"Will report passing {fixName}");
    }

    private static bool HasPatternCircuit(AircraftState aircraft) =>
        aircraft.Phases?.Phases.Any(p => p is UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase) == true;

    private static void SetPatternLegReportArmed(AircraftApproachState approach, ReportTrigger leg, bool armed)
    {
        switch (leg)
        {
            case ReportTrigger.Crosswind:
                approach.ReportArmedCrosswind = armed;
                break;
            case ReportTrigger.Downwind:
                approach.ReportArmedDownwind = armed;
                break;
            case ReportTrigger.Base:
                approach.ReportArmedBase = armed;
                break;
            case ReportTrigger.Final:
                approach.ReportArmedFinal = armed;
                break;
            case ReportTrigger.Midfield:
                approach.ReportArmedMidfield = armed;
                break;
            default:
                break;
        }
    }

    /// <summary>How a pattern-leg report names itself: "turning crosswind", or "midfield downwind".</summary>
    private static string PatternReportPhrase(ReportTrigger leg) =>
        leg == ReportTrigger.Midfield ? ReportLegWord(leg) : $"turning {ReportLegWord(leg)}";

    /// <summary>The echo for an armed pattern-leg report: "Will report midfield downwind", else "Will report turning {leg}".</summary>
    private static string PatternReportArmedEcho(ReportTrigger leg) => $"Will report {PatternReportPhrase(leg)}";

    /// <summary>The echo for a cancelled pattern-leg report: "Cancelled midfield downwind report", else "Cancelled turning {leg} report".</summary>
    private static string PatternReportCancelledEcho(ReportTrigger leg) => $"Cancelled {PatternReportPhrase(leg)} report";

    private static string ReportLegWord(ReportTrigger leg) =>
        leg switch
        {
            ReportTrigger.Crosswind => "crosswind",
            ReportTrigger.Downwind => "downwind",
            ReportTrigger.Base => "base",
            ReportTrigger.Final => "final",
            ReportTrigger.Midfield => "midfield downwind",
            _ => "final",
        };
}

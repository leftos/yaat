using System.Text.Json.Serialization;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public enum TurnDirection
{
    Left,
    Right,
}

public class ControlTargets
{
    /// <summary>Target heading in degrees true. Physics steers toward this value.</summary>
    public TrueHeading? TargetTrueHeading { get; set; }

    /// <summary>
    /// Left/Right override; null means shortest path.
    /// </summary>
    public TurnDirection? PreferredTurnDirection { get; set; }

    /// <summary>
    /// Turn rate override in deg/sec. Null means use category default.
    /// Set by pattern phases to use the tighter pattern turn rate.
    /// </summary>
    public double? TurnRateOverride { get; set; }

    /// <summary>Target altitude in feet MSL.</summary>
    public double? TargetAltitude { get; set; }

    /// <summary>Minimum altitude in feet MSL. Used for "maintain VFR at or above" restrictions.</summary>
    public double? AltitudeFloor { get; set; }

    /// <summary>Maximum altitude in feet MSL. Used for "maintain VFR at or below" restrictions.</summary>
    public double? AltitudeCeiling { get; set; }

    /// <summary>
    /// Vertical rate override in fpm (positive = climb).
    /// Null means use category default.
    /// </summary>
    public double? DesiredVerticalRate { get; set; }

    /// <summary>
    /// Target indicated airspeed in knots.
    /// Null means maintain current speed.
    /// </summary>
    public double? TargetSpeed { get; set; }

    /// <summary>
    /// Deceleration rate override in knots/sec (positive = decel). Null means use
    /// the category default from <see cref="AircraftPerformance.DecelRate"/>. Set by
    /// LandingPhase / RunwayExitPhase during ground rollout when kinematic
    /// firm-braking is required. <see cref="FlightPhysics.UpdateSpeed"/> reads this
    /// when decelerating toward <see cref="TargetSpeed"/>; it is ignored on the
    /// acceleration branch. Cleared on phase transition.
    /// </summary>
    public double? DesiredDecelRate { get; set; }

    /// <summary>Minimum IAS in knots (speed floor, "maintain X or greater"). Enforced continuously.</summary>
    public double? SpeedFloor { get; set; }

    /// <summary>Maximum IAS in knots (speed ceiling, "do not exceed X"). Enforced continuously.</summary>
    public double? SpeedCeiling { get; set; }

    /// <summary>Controller-assigned heading in magnetic (FH/TL/TR/FPH/PTAC). Null when not explicitly vectored.</summary>
    public MagneticHeading? AssignedMagneticHeading { get; set; }

    /// <summary>Controller-assigned altitude (CM/DM). Null when not explicitly assigned.</summary>
    public double? AssignedAltitude { get; set; }

    /// <summary>Controller-assigned speed (SPD/SLOW/RFAS). Null when not explicitly assigned.</summary>
    public double? AssignedSpeed { get; set; }

    /// <summary>
    /// True when the controller has issued an explicit SPD command.
    /// Prevents altitude-based auto speed scheduling from overriding
    /// the controller's instruction. Cleared on altitude commands
    /// without an accompanying speed.
    /// </summary>
    public bool HasExplicitSpeedCommand { get; set; }

    /// <summary>
    /// True when a forced speed assignment (SPEEDF, or the SPDN teleport) has
    /// deliberately overridden the §5-7-1.b.4 "no speed inside 5nm final" rule.
    /// Exempts the assignment from <see cref="FlightPhysics"/>'s auto-cancel at the
    /// final gate so the controller's forced speed persists. Cleared by any plain
    /// SPD/RNS/RFAS/DSR/Mach command.
    /// </summary>
    public bool SpeedOverridesFinalGate { get; set; }

    /// <summary>
    /// True when the user has issued an explicit TRATE command.
    /// Prevents pattern phases from overwriting TurnRateOverride.
    /// Cleared on TRATE (no arg), Warp, WarpGround, and phase-clear.
    /// </summary>
    public bool HasExplicitTurnRate { get; set; }

    /// <summary>
    /// Target Mach number. When set, UpdateSpeed recomputes equivalent IAS each tick
    /// so the aircraft maintains constant Mach as altitude changes.
    /// </summary>
    public double? TargetMach { get; set; }

    /// <summary>
    /// Waypoint queue for DCT (direct-to) navigation.
    /// The aircraft steers toward the first waypoint; when reached,
    /// it advances to the next. Cleared when all waypoints are visited.
    /// </summary>
    public List<NavigationTarget> NavigationRoute { get; } = [];

    public ControlTargetsDto ToSnapshot() =>
        new()
        {
            TargetTrueHeadingDeg = TargetTrueHeading?.Degrees,
            PreferredTurnDirection = PreferredTurnDirection.HasValue ? (int)PreferredTurnDirection.Value : null,
            TurnRateOverride = TurnRateOverride,
            TargetAltitude = TargetAltitude,
            AltitudeFloor = AltitudeFloor,
            AltitudeCeiling = AltitudeCeiling,
            DesiredVerticalRate = DesiredVerticalRate,
            TargetSpeed = TargetSpeed,
            DesiredDecelRate = DesiredDecelRate,
            SpeedFloor = SpeedFloor,
            SpeedCeiling = SpeedCeiling,
            AssignedMagneticHeadingDeg = AssignedMagneticHeading?.Degrees,
            AssignedAltitude = AssignedAltitude,
            AssignedSpeed = AssignedSpeed,
            HasExplicitSpeedCommand = HasExplicitSpeedCommand,
            SpeedOverridesFinalGate = SpeedOverridesFinalGate,
            HasExplicitTurnRate = HasExplicitTurnRate,
            TargetMach = TargetMach,
            NavigationRoute = NavigationRoute.Count > 0 ? NavigationRoute.Select(n => n.ToSnapshot()).ToList() : null,
        };

    public static void RestoreFrom(ControlTargetsDto dto, ControlTargets targets)
    {
        targets.TargetTrueHeading = dto.TargetTrueHeadingDeg.HasValue ? new TrueHeading(dto.TargetTrueHeadingDeg.Value) : null;
        targets.PreferredTurnDirection = dto.PreferredTurnDirection.HasValue ? (TurnDirection)dto.PreferredTurnDirection.Value : null;
        targets.TurnRateOverride = dto.TurnRateOverride;
        targets.TargetAltitude = dto.TargetAltitude;
        targets.AltitudeFloor = dto.AltitudeFloor;
        targets.AltitudeCeiling = dto.AltitudeCeiling;
        targets.DesiredVerticalRate = dto.DesiredVerticalRate;
        targets.TargetSpeed = dto.TargetSpeed;
        targets.DesiredDecelRate = dto.DesiredDecelRate;
        targets.SpeedFloor = dto.SpeedFloor;
        targets.SpeedCeiling = dto.SpeedCeiling;
        targets.AssignedMagneticHeading = dto.AssignedMagneticHeadingDeg.HasValue ? new MagneticHeading(dto.AssignedMagneticHeadingDeg.Value) : null;
        targets.AssignedAltitude = dto.AssignedAltitude;
        targets.AssignedSpeed = dto.AssignedSpeed;
        targets.HasExplicitSpeedCommand = dto.HasExplicitSpeedCommand;
        targets.SpeedOverridesFinalGate = dto.SpeedOverridesFinalGate;
        targets.HasExplicitTurnRate = dto.HasExplicitTurnRate;
        targets.TargetMach = dto.TargetMach;
        targets.NavigationRoute.Clear();
        if (dto.NavigationRoute is not null)
        {
            foreach (var nav in dto.NavigationRoute)
            {
                targets.NavigationRoute.Add(NavigationTarget.FromSnapshot(nav));
            }
        }
    }
}

public class NavigationTarget
{
    public required string Name { get; init; }

    /// <summary>
    /// True when <paramref name="name"/> is a synthetic arc-densification vertex (<c>ARC01</c>,
    /// <c>ARC02</c>, …) emitted when an RF/AF (DME arc) leg is expanded into a polyline by the arc
    /// expanders in <c>DepartureClearanceHandler</c>/<c>ApproachCommandHandler</c>. These vertices
    /// are not real fixes — route projections skip them for name display and the radar overlay draws
    /// them as bare path points (no diamond, label, or restriction).
    /// </summary>
    public static bool IsSyntheticArcName(string name) =>
        name.Length == 5
        && (name[0] == 'A' || name[0] == 'a')
        && (name[1] == 'R' || name[1] == 'r')
        && (name[2] == 'C' || name[2] == 'c')
        && char.IsAsciiDigit(name[3])
        && char.IsAsciiDigit(name[4]);

    /// <summary>Geographic position of the navigation fix.</summary>
    public required LatLon Position { get; init; }

    public CifpAltitudeRestriction? AltitudeRestriction { get; set; }
    public CifpSpeedRestriction? SpeedRestriction { get; set; }
    public bool IsFlyOver { get; init; }

    /// <summary>
    /// Published outbound course (magnetic) to fly after sequencing this terminating fix when the
    /// route is then empty — the ARINC-424 FM "fly course, expect vectors" leg that ends most US
    /// STARs. Null for ordinary fixes (the aircraft holds its arrival heading at route end).
    /// </summary>
    public double? TerminalCourseMagnetic { get; set; }

    /// <summary>TargetAltitude to restore after sequencing past this fix (CFIX/drawn-route revert).</summary>
    public double? RevertAltitude { get; set; }

    /// <summary>AssignedAltitude to restore after sequencing past this fix.</summary>
    public double? RevertAssignedAltitude { get; set; }

    /// <summary>TargetSpeed to restore after sequencing past this fix.</summary>
    public double? RevertSpeed { get; set; }

    /// <summary>AssignedSpeed to restore after sequencing past this fix.</summary>
    public double? RevertAssignedSpeed { get; set; }

    public NavigationTargetDto ToSnapshot() =>
        new()
        {
            Name = Name,
            Position = Position,
            AltitudeRestriction = AltitudeRestriction is not null
                ? new AltitudeRestrictionDto
                {
                    Type = (int)AltitudeRestriction.Type,
                    Altitude1 = AltitudeRestriction.Altitude1Ft,
                    Altitude2 = AltitudeRestriction.Altitude2Ft,
                }
                : null,
            SpeedRestriction = SpeedRestriction is not null
                ? new SpeedRestrictionDto { Type = SpeedRestrictionDto.ToTypeCode(SpeedRestriction.Type), Speed = SpeedRestriction.SpeedKts }
                : null,
            IsFlyOver = IsFlyOver,
            RevertAltitude = RevertAltitude,
            RevertAssignedAltitude = RevertAssignedAltitude,
            RevertSpeed = RevertSpeed,
            RevertAssignedSpeed = RevertAssignedSpeed,
            TerminalCourseMagnetic = TerminalCourseMagnetic,
        };

    public static NavigationTarget FromSnapshot(NavigationTargetDto dto) =>
        new()
        {
            Name = dto.Name,
            Position = dto.Position,
            AltitudeRestriction = dto.AltitudeRestriction is not null
                ? new CifpAltitudeRestriction(
                    (CifpAltitudeRestrictionType)dto.AltitudeRestriction.Type,
                    dto.AltitudeRestriction.Altitude1,
                    dto.AltitudeRestriction.Altitude2
                )
                : null,
            SpeedRestriction = dto.SpeedRestriction is not null
                ? new CifpSpeedRestriction(dto.SpeedRestriction.Speed, SpeedRestrictionDto.FromTypeCode(dto.SpeedRestriction.Type))
                : null,
            IsFlyOver = dto.IsFlyOver,
            RevertAltitude = dto.RevertAltitude,
            RevertAssignedAltitude = dto.RevertAssignedAltitude,
            RevertSpeed = dto.RevertSpeed,
            RevertAssignedSpeed = dto.RevertAssignedSpeed,
            TerminalCourseMagnetic = dto.TerminalCourseMagnetic,
        };
}

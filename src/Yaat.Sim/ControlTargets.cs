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
            DesiredVerticalRate = DesiredVerticalRate,
            TargetSpeed = TargetSpeed,
            SpeedFloor = SpeedFloor,
            SpeedCeiling = SpeedCeiling,
            AssignedMagneticHeadingDeg = AssignedMagneticHeading?.Degrees,
            AssignedAltitude = AssignedAltitude,
            AssignedSpeed = AssignedSpeed,
            HasExplicitSpeedCommand = HasExplicitSpeedCommand,
            TargetMach = TargetMach,
            NavigationRoute = NavigationRoute.Count > 0 ? NavigationRoute.Select(n => n.ToSnapshot()).ToList() : null,
        };

    public static void RestoreFrom(ControlTargetsDto dto, ControlTargets targets)
    {
        targets.TargetTrueHeading = dto.TargetTrueHeadingDeg.HasValue ? new TrueHeading(dto.TargetTrueHeadingDeg.Value) : null;
        targets.PreferredTurnDirection = dto.PreferredTurnDirection.HasValue ? (TurnDirection)dto.PreferredTurnDirection.Value : null;
        targets.TurnRateOverride = dto.TurnRateOverride;
        targets.TargetAltitude = dto.TargetAltitude;
        targets.DesiredVerticalRate = dto.DesiredVerticalRate;
        targets.TargetSpeed = dto.TargetSpeed;
        targets.SpeedFloor = dto.SpeedFloor;
        targets.SpeedCeiling = dto.SpeedCeiling;
        targets.AssignedMagneticHeading = dto.AssignedMagneticHeadingDeg.HasValue ? new MagneticHeading(dto.AssignedMagneticHeadingDeg.Value) : null;
        targets.AssignedAltitude = dto.AssignedAltitude;
        targets.AssignedSpeed = dto.AssignedSpeed;
        targets.HasExplicitSpeedCommand = dto.HasExplicitSpeedCommand;
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
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public CifpAltitudeRestriction? AltitudeRestriction { get; set; }
    public CifpSpeedRestriction? SpeedRestriction { get; set; }
    public bool IsFlyOver { get; init; }

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
            Latitude = Latitude,
            Longitude = Longitude,
            AltitudeRestriction = AltitudeRestriction is not null
                ? new AltitudeRestrictionDto
                {
                    Type = (int)AltitudeRestriction.Type,
                    Altitude1 = AltitudeRestriction.Altitude1Ft,
                    Altitude2 = AltitudeRestriction.Altitude2Ft,
                }
                : null,
            SpeedRestriction = SpeedRestriction is not null
                ? new SpeedRestrictionDto { Type = SpeedRestriction.IsMaximum ? 1 : 0, Speed = SpeedRestriction.SpeedKts }
                : null,
            IsFlyOver = IsFlyOver,
            RevertAltitude = RevertAltitude,
            RevertAssignedAltitude = RevertAssignedAltitude,
            RevertSpeed = RevertSpeed,
            RevertAssignedSpeed = RevertAssignedSpeed,
        };

    public static NavigationTarget FromSnapshot(NavigationTargetDto dto) =>
        new()
        {
            Name = dto.Name,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            AltitudeRestriction = dto.AltitudeRestriction is not null
                ? new CifpAltitudeRestriction(
                    (CifpAltitudeRestrictionType)dto.AltitudeRestriction.Type,
                    dto.AltitudeRestriction.Altitude1,
                    dto.AltitudeRestriction.Altitude2
                )
                : null,
            SpeedRestriction = dto.SpeedRestriction is not null
                ? new CifpSpeedRestriction(dto.SpeedRestriction.Speed, dto.SpeedRestriction.Type == 1)
                : null,
            IsFlyOver = dto.IsFlyOver,
            RevertAltitude = dto.RevertAltitude,
            RevertAssignedAltitude = dto.RevertAssignedAltitude,
            RevertSpeed = dto.RevertSpeed,
            RevertAssignedSpeed = dto.RevertAssignedSpeed,
        };
}

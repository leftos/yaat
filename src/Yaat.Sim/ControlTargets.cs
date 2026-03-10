using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim;

public enum TurnDirection
{
    Left,
    Right,
}

public class ControlTargets
{
    /// <summary>Target heading in degrees magnetic.</summary>
    public double? TargetHeading { get; set; }

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

    /// <summary>
    /// True when the controller has issued an explicit SPD command.
    /// Prevents altitude-based auto speed scheduling from overriding
    /// the controller's instruction. Cleared on altitude commands
    /// without an accompanying speed.
    /// </summary>
    public bool HasExplicitSpeedCommand { get; set; }

    /// <summary>
    /// Waypoint queue for DCT (direct-to) navigation.
    /// The aircraft steers toward the first waypoint; when reached,
    /// it advances to the next. Cleared when all waypoints are visited.
    /// </summary>
    public List<NavigationTarget> NavigationRoute { get; } = [];
}

public class NavigationTarget
{
    public required string Name { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public CifpAltitudeRestriction? AltitudeRestriction { get; init; }
    public CifpSpeedRestriction? SpeedRestriction { get; init; }
    public bool IsFlyOver { get; init; }
}

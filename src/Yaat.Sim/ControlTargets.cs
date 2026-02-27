namespace Yaat.Sim;

public enum TurnDirection
{
    Left,
    Right
}

public class ControlTargets
{
    /// <summary>Target heading in degrees magnetic.</summary>
    public double? TargetHeading { get; set; }

    /// <summary>
    /// Left/Right override; null means shortest path.
    /// </summary>
    public TurnDirection? PreferredTurnDirection { get; set; }

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
}

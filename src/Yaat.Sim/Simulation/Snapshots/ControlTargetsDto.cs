namespace Yaat.Sim.Simulation.Snapshots;

public sealed class ControlTargetsDto
{
    public double? TargetTrueHeadingDeg { get; init; }
    public int? PreferredTurnDirection { get; init; }
    public double? TurnRateOverride { get; init; }
    public double? TargetAltitude { get; init; }
    public double? DesiredVerticalRate { get; init; }
    public double? TargetSpeed { get; init; }
    public double? SpeedFloor { get; init; }
    public double? SpeedCeiling { get; init; }
    public double? AssignedMagneticHeadingDeg { get; init; }
    public double? AssignedAltitude { get; init; }
    public double? AssignedSpeed { get; init; }
    public required bool HasExplicitSpeedCommand { get; init; }
    public double? TargetMach { get; init; }
    public List<NavigationTargetDto>? NavigationRoute { get; init; }
}

public sealed class NavigationTargetDto
{
    public required string Name { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public AltitudeRestrictionDto? AltitudeRestriction { get; init; }
    public SpeedRestrictionDto? SpeedRestriction { get; init; }
    public required bool IsFlyOver { get; init; }
    public double? RevertAltitude { get; init; }
    public double? RevertAssignedAltitude { get; init; }
    public double? RevertSpeed { get; init; }
    public double? RevertAssignedSpeed { get; init; }
}

public sealed class AltitudeRestrictionDto
{
    public required int Type { get; init; }
    public required int Altitude1 { get; init; }
    public int? Altitude2 { get; init; }
}

public sealed class SpeedRestrictionDto
{
    public required int Type { get; init; }
    public required int Speed { get; init; }
}

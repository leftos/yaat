namespace Yaat.Sim.Simulation.Snapshots;

public sealed class ControlTargetsDto
{
    public double? TargetTrueHeadingDeg { get; init; }
    public int? PreferredTurnDirection { get; init; }
    public double? TurnRateOverride { get; init; }
    public double? TargetAltitude { get; init; }
    public double? AltitudeFloor { get; init; }
    public double? AltitudeCeiling { get; init; }
    public double? DesiredVerticalRate { get; init; }
    public double? TargetSpeed { get; init; }
    public double? DesiredDecelRate { get; init; }
    public double? SpeedFloor { get; init; }
    public double? SpeedCeiling { get; init; }
    public double? AssignedMagneticHeadingDeg { get; init; }
    public double? AssignedAltitude { get; init; }
    public double? AssignedSpeed { get; init; }
    public required bool HasExplicitSpeedCommand { get; init; }
    public bool SpeedOverridesFinalGate { get; init; }
    public bool HasExplicitTurnRate { get; init; }
    public double? TargetMach { get; init; }
    public List<NavigationTargetDto>? NavigationRoute { get; init; }
}

public sealed class NavigationTargetDto
{
    public required string Name { get; init; }
    public required LatLon Position { get; init; }
    public AltitudeRestrictionDto? AltitudeRestriction { get; init; }
    public SpeedRestrictionDto? SpeedRestriction { get; init; }
    public required bool IsFlyOver { get; init; }
    public double? RevertAltitude { get; init; }
    public double? RevertAssignedAltitude { get; init; }
    public double? RevertSpeed { get; init; }
    public double? RevertAssignedSpeed { get; init; }
    public double? TerminalCourseMagnetic { get; init; }
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

    // Legacy snapshots only ever wrote Type=1 (the old IsMaximum=true), so 0 and 1 both map back to
    // AtOrBelow; new restriction types use distinct codes. Kept in one place so every snapshot
    // round-trip that carries a speed restriction shares the same wire mapping.
    public static int ToTypeCode(Data.Vnas.CifpSpeedRestrictionType type) =>
        type switch
        {
            Data.Vnas.CifpSpeedRestrictionType.AtOrAbove => 2,
            Data.Vnas.CifpSpeedRestrictionType.Mandatory => 3,
            _ => 1,
        };

    public static Data.Vnas.CifpSpeedRestrictionType FromTypeCode(int type) =>
        type switch
        {
            2 => Data.Vnas.CifpSpeedRestrictionType.AtOrAbove,
            3 => Data.Vnas.CifpSpeedRestrictionType.Mandatory,
            _ => Data.Vnas.CifpSpeedRestrictionType.AtOrBelow,
        };
}

public sealed class ProcedureLegDto
{
    public required int Type { get; init; }
    public string? FixName { get; init; }
    public LatLon? FixPosition { get; init; }
    public double? CourseMagnetic { get; init; }
    public double? TargetAltitudeFt { get; init; }
    public bool TerminatesOnNextLegIntercept { get; init; }
    public string? Turn { get; init; }
    public AltitudeRestrictionDto? AltitudeRestriction { get; init; }
    public SpeedRestrictionDto? SpeedRestriction { get; init; }
    public required bool IsFlyOver { get; init; }
    public LatLon? TerminationReferencePosition { get; init; }
    public double? TerminationDistanceNm { get; init; }
    public double? TerminationRadialMagnetic { get; init; }
}

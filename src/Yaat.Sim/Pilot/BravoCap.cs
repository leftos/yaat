namespace Yaat.Sim.Pilot;

/// <summary>
/// The pilot's own cap beneath a Class B shelf while a phase runs (14 CFR 91.131; AIM 3-2-3.b): an uncleared VFR
/// aircraft levels below the shelf floor rather than climb into the Class B. Owned by
/// <see cref="AircraftState.BravoCap"/>, which alone writes <see cref="ControlTargets.AltitudeCeiling"/> for it as
/// <see cref="Ceiling"/>. It is not a controller bound: <see cref="AircraftState.ControllerAltitudeCeiling"/> reads past it.
/// </summary>
/// <param name="AreaName">The Class B's area name (<see cref="Data.Airspace.AirspaceDatabase.ClassBAreaName"/>).</param>
/// <param name="VolumeId">The <see cref="Data.Airspace.AirspaceVolume.Id"/> of the shelf the cap sits under.</param>
/// <param name="CapFtMsl">The level beneath the shelf floor, as the free-flight level-off computes it.</param>
/// <param name="PriorCeiling">The ceiling in force before the cap: a controller's at-or-below bound, else none.</param>
/// <param name="PriorTargetAltitude">The target altitude before the cap, put back on release when physics has nulled it.</param>
public sealed record BravoCap(string AreaName, string VolumeId, int CapFtMsl, double? PriorCeiling, double? PriorTargetAltitude)
{
    /// <summary>The ceiling the cap writes: the lower of the prior ceiling and the cap.</summary>
    public double Ceiling => (PriorCeiling is { } prior) ? Math.Min(prior, CapFtMsl) : CapFtMsl;
}

using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Data;

/// <summary>
/// Wraps another <see cref="IProfileCorrectionAdapter"/> (normally the
/// <see cref="EurocontrolProfileCorrectionAdapter"/>) and makes authoritative overrides win.
/// For each correctable field, if <see cref="AircraftProfileDatabase.IsOverridden"/> reports the
/// field was set by an <see cref="AircraftProfileOverride"/>, the merged profile value is returned
/// verbatim; otherwise the inner adapter's runtime correction applies.
///
/// This is what lets a hand-curated correction stick: the SF50's ~170 kt initial climb is an
/// override, so it bypasses the Eurocontrol cap (FAA ACD Vref 87 × 1.40 ≈ 122 kt) that would
/// otherwise pull it down.
/// </summary>
public sealed class OverrideAwareProfileCorrectionAdapter(IProfileCorrectionAdapter inner) : IProfileCorrectionAdapter
{
    public double FinalApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd) =>
        AircraftProfileDatabase.IsOverridden(profile.TypeCode, nameof(AircraftProfile.FinalApproachSpeed))
            ? profile.FinalApproachSpeed
            : inner.FinalApproachSpeed(profile, acd);

    public double PatternSpeed(AircraftProfile profile, FaaAircraftRecord? acd) =>
        AircraftProfileDatabase.IsOverridden(profile.TypeCode, nameof(AircraftProfile.PatternSpeed))
            ? profile.PatternSpeed
            : inner.PatternSpeed(profile, acd);

    /// <summary>
    /// Base-leg speed is the midpoint of pattern and final approach. Derived from this adapter's
    /// own corrected values so an override on either pattern or final speed flows through.
    /// </summary>
    public double BaseSpeed(AircraftProfile profile, FaaAircraftRecord? acd) => (PatternSpeed(profile, acd) + FinalApproachSpeed(profile, acd)) / 2.0;

    public double InitialApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd) =>
        AircraftProfileDatabase.IsOverridden(profile.TypeCode, nameof(AircraftProfile.InitialApproachSpeed))
            ? profile.InitialApproachSpeed
            : inner.InitialApproachSpeed(profile, acd);

    public double ClimbSpeedInitial(AircraftProfile profile, FaaAircraftRecord? acd) =>
        AircraftProfileDatabase.IsOverridden(profile.TypeCode, nameof(AircraftProfile.ClimbSpeedInitial))
            ? profile.ClimbSpeedInitial
            : inner.ClimbSpeedInitial(profile, acd);

    public double ClimbRateInitial(AircraftProfile profile, FaaAircraftRecord? acd) =>
        AircraftProfileDatabase.IsOverridden(profile.TypeCode, nameof(AircraftProfile.ClimbRateInitial))
            ? profile.ClimbRateInitial
            : inner.ClimbRateInitial(profile, acd);
}

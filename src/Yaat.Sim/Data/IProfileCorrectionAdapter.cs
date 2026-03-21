using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Data;

/// <summary>
/// Adapter that can adjust AircraftProfile values at runtime using external data
/// sources. AircraftPerformance delegates to the installed adapter for correctable
/// fields; the default <see cref="PassthroughProfileCorrectionAdapter"/> returns
/// profile values unchanged.
///
/// To install a correction adapter, call
/// <see cref="AircraftPerformance.SetProfileCorrectionAdapter"/> at startup.
/// </summary>
public interface IProfileCorrectionAdapter
{
    double FinalApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd);
    double PatternSpeed(AircraftProfile profile, FaaAircraftRecord? acd);
    double BaseSpeed(AircraftProfile profile, FaaAircraftRecord? acd);
    double InitialApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd);
    double ClimbSpeedInitial(AircraftProfile profile, FaaAircraftRecord? acd);
    double ClimbRateInitial(AircraftProfile profile, FaaAircraftRecord? acd);
}

/// <summary>
/// Default adapter: returns all profile values unchanged. Installed automatically
/// when no correction adapter is configured. All multipliers are effectively 1.0.
/// </summary>
public sealed class PassthroughProfileCorrectionAdapter : IProfileCorrectionAdapter
{
    public double FinalApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd) => profile.FinalApproachSpeed;

    public double PatternSpeed(AircraftProfile profile, FaaAircraftRecord? acd) => profile.PatternSpeed;

    public double BaseSpeed(AircraftProfile profile, FaaAircraftRecord? acd) => (profile.PatternSpeed + profile.FinalApproachSpeed) / 2.0;

    public double InitialApproachSpeed(AircraftProfile profile, FaaAircraftRecord? acd) => profile.InitialApproachSpeed;

    public double ClimbSpeedInitial(AircraftProfile profile, FaaAircraftRecord? acd) => profile.ClimbSpeedInitial;

    public double ClimbRateInitial(AircraftProfile profile, FaaAircraftRecord? acd) => profile.ClimbRateInitial;
}

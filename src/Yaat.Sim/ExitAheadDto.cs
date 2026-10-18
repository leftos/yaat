using Yaat.Sim.Phases;

namespace Yaat.Sim;

/// <summary>
/// One named exit ahead an arrival can make, as the exits-ahead list offers it (<c>LandingPhase.ListExitsAhead</c> on the rollout,
/// <c>FinalApproachExitForecast.ListExitsAhead</c> on the last miles of final): <see cref="Taxiway"/> turned off on
/// <see cref="Side"/> — the command it stands for is <c>EL</c>/<c>ER</c> with that taxiway — <see cref="DistanceFt"/> along the
/// runway to the connection's branch point (from the aircraft on the rollout, from the landing threshold on final), already
/// rounded to the nearest 100 ft, <see cref="Planned"/> for the exit the aircraft means to take: the one it is braking for on
/// the rollout, the controller's exit instruction on final, and <see cref="TurnOffAngleDeg"/>, the exit's turn-off angle from
/// the runway's direction of travel in whole degrees (0 along the runway, 90 square, over 90 bending back), as the ground
/// layout's exit search measures it (<c>CenterlineExitResult.ExitAngle</c>, which stands at 90 for an exit it cannot measure).
/// </summary>
public sealed record ExitAheadDto(string Taxiway, ExitSide Side, int DistanceFt, bool Planned, int TurnOffAngleDeg);

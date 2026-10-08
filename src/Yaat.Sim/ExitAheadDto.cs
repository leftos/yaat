using Yaat.Sim.Phases;

namespace Yaat.Sim;

/// <summary>
/// One named exit ahead an arrival can make, as the exits-ahead list offers it (<c>LandingPhase.ListExitsAhead</c> on the rollout,
/// <c>FinalApproachExitForecast.ListExitsAhead</c> on the last miles of final): <see cref="Taxiway"/> turned off on
/// <see cref="Side"/> — the command it stands for is <c>EL</c>/<c>ER</c> with that taxiway — <see cref="DistanceFt"/> along the
/// runway to the connection's branch point (from the aircraft on the rollout, from the landing threshold on final), already
/// rounded to the nearest 100 ft, and <see cref="Planned"/> for the exit the aircraft means to take: the one it is braking for on
/// the rollout, the controller's exit instruction on final.
/// </summary>
public sealed record ExitAheadDto(string Taxiway, ExitSide Side, int DistanceFt, bool Planned);

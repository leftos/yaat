namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The result of <c>get_sim_time</c>: the scenario-elapsed seconds the client shows (the replay position in playback), whether
/// the room's sim is paused, and its sim rate.
/// </summary>
public sealed record GetSimTimeResult(double SimSeconds, bool IsPaused, int SimRate);

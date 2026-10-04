namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The result of <c>wait_for</c>: the milliseconds until the condition held, and how many matches met it (0 for
/// <c>not_exists</c>).
/// </summary>
public sealed record WaitForResult(int ElapsedMs, int MatchCount);

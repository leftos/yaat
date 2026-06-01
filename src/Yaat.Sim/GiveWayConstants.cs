namespace Yaat.Sim;

/// <summary>
/// Auto-release tuning for direct GIVEWAY holds, applied in
/// <see cref="FlightPhysics.UpdateGiveWayResume"/>. These govern the safety-net
/// release paths layered on top of the pure bearing/heading geometry in
/// <see cref="FlightPhysics.IsGiveWayMet"/>; they do not affect deferred
/// <c>BEHIND</c> conditions, which keep pure-geometry release.
/// </summary>
internal static class GiveWayConstants
{
    /// <summary>
    /// A GIVEWAY hold older than this auto-releases as a safety net — e.g. a
    /// typo'd-but-real distant target that will never satisfy the pass geometry.
    /// </summary>
    public const double SafetyTimeoutSeconds = 300.0;

    /// <summary>
    /// The yield target must be stopped at least this long before the
    /// stalemate-bypass fallback may release the held aircraft.
    /// </summary>
    public const double TargetStationaryThresholdSeconds = 30.0;

    /// <summary>Ground speed (kts) at or below which an aircraft counts as stationary for the idle timer.</summary>
    public const double StationarySpeedThresholdKts = 1.0;

    /// <summary>
    /// On safety timeout, the aircraft only force-releases if it has lateral room to pass the
    /// target OR the target is farther than this — so the timeout can't drive an aircraft into a
    /// merely-slow target sitting close and dead ahead. A phantom/typo'd distant target clears it.
    /// </summary>
    public const double SafetyTimeoutClearDistanceNm = 0.25;
}

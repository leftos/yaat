namespace Yaat.Sim.Commands;

/// <summary>
/// Resolves an optional HHMM Zulu release time to an absolute-UTC <see cref="ReleaseWindow"/>. Pure and
/// side-effect-free; callers pass <c>nowUtc</c> explicitly so the logic is deterministic under test. See
/// <see cref="ReleaseWindow"/> for the wall-clock caveat.
/// </summary>
public static class CfrWindowResolver
{
    /// <summary>
    /// Builds the FAA −2/+1 release window (<see cref="CfrWindow"/>) around an assigned Zulu time. When
    /// <paramref name="hhmm"/> is given the assigned time is the UTC instant nearest to
    /// <paramref name="nowUtc"/> matching HH:MM; when it is null the release is <b>immediate</b> — the
    /// assigned time is <see cref="CfrWindow.WindowBeforeSeconds"/> out (≈ 2 min), so the window opens
    /// right now. Assumes a validated HHMM (0000..2359) — the command parser enforces the range.
    /// </summary>
    public static ReleaseWindow Resolve(int? hhmm, DateTime nowUtc)
    {
        var assigned = hhmm is null
            ? nowUtc.AddSeconds(CfrWindow.WindowBeforeSeconds)
            : ResolveNearestUtc(hhmm.Value / 100, hhmm.Value % 100, nowUtc);
        return new ReleaseWindow(assigned.AddSeconds(-CfrWindow.WindowBeforeSeconds), assigned.AddSeconds(CfrWindow.WindowAfterSeconds));
    }

    /// <summary>
    /// Resolves an HH:MM time-of-day to the absolute UTC instant nearest <paramref name="nowUtc"/>, checking
    /// the prior/current/next UTC day so a time just across midnight rolls over correctly (e.g. CFR 0001 at
    /// 2358Z resolves to 00:01 the next day, not 24 h ago).
    /// </summary>
    internal static DateTime ResolveNearestUtc(int hours, int minutes, DateTime nowUtc)
    {
        var onDay = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hours, minutes, 0, DateTimeKind.Utc);
        var best = onDay;
        foreach (var candidate in new[] { onDay.AddDays(-1), onDay.AddDays(1) })
        {
            if (Math.Abs((candidate - nowUtc).Ticks) < Math.Abs((best - nowUtc).Ticks))
            {
                best = candidate;
            }
        }

        return best;
    }
}

/// <summary>
/// Evaluates a <see cref="ReleaseWindow"/> against wall-clock time and the aircraft's ground state,
/// returning the single violation to alert on (or null). <paramref name="alreadyFired"/> is the
/// per-aircraft latch; a kind already latched returns null so each alert fires once.
/// </summary>
public static class CfrAlertEvaluator
{
    /// <summary>
    /// Wheels-up (<paramref name="wasOnGround"/> and not <paramref name="isOnGround"/>): before the window
    /// start → <see cref="CfrAlertKind.EarlyTakeoff"/>; after the end → <see cref="CfrAlertKind.LateTakeoff"/>;
    /// in-window → null. Still on the ground past the end → <see cref="CfrAlertKind.ExpiredGrounded"/>.
    /// </summary>
    public static CfrAlertKind? Evaluate(ReleaseWindow window, DateTime nowUtc, bool isOnGround, bool wasOnGround, CfrAlertKind alreadyFired)
    {
        if (wasOnGround && !isOnGround)
        {
            if (nowUtc < window.StartUtc)
            {
                return alreadyFired.HasFlag(CfrAlertKind.EarlyTakeoff) ? null : CfrAlertKind.EarlyTakeoff;
            }

            if (nowUtc > window.EndUtc)
            {
                return alreadyFired.HasFlag(CfrAlertKind.LateTakeoff) ? null : CfrAlertKind.LateTakeoff;
            }

            return null;
        }

        if (isOnGround && nowUtc > window.EndUtc)
        {
            return alreadyFired.HasFlag(CfrAlertKind.ExpiredGrounded) ? null : CfrAlertKind.ExpiredGrounded;
        }

        return null;
    }
}

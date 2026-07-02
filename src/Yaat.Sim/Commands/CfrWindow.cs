namespace Yaat.Sim.Commands;

/// <summary>
/// An absolute-UTC release window for a Call-For-Release departure. Wall-clock based and deliberately
/// unanchored from sim time — used only for instructor-facing expiry alerts, never for simulation
/// behavior, so pause/rewind/fast-forward may make it inconsistent (GitHub issue #230).
/// </summary>
public readonly record struct ReleaseWindow(DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// The Call-For-Release compliance window, fixed by FAA JO 7110.65 §4-3-4.e.5: when CFR is in effect,
/// release aircraft so they are airborne within a window extending from 2 minutes prior to 1 minute
/// after the assigned time. Not a user preference — it is set by regulation.
/// </summary>
public static class CfrWindow
{
    /// <summary>Seconds before the assigned Zulu time the window opens (FAA 7110.65 §4-3-4.e.5: 2 min).</summary>
    public const int WindowBeforeSeconds = 120;

    /// <summary>Seconds after the assigned Zulu time the window closes (FAA 7110.65 §4-3-4.e.5: 1 min).</summary>
    public const int WindowAfterSeconds = 60;
}

/// <summary>
/// Which release-window violation an aircraft has tripped. Latched per aircraft on the client so each
/// alert fires once.
/// </summary>
[Flags]
public enum CfrAlertKind
{
    None = 0,
    EarlyTakeoff = 1,
    LateTakeoff = 2,
    ExpiredGrounded = 4,
}

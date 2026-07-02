using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Applies a Call-For-Release (<c>CFR</c>) command to an aircraft's release-time window.
/// The window is absolute-UTC and wall-clock based; it drives instructor-facing expiry alerts on the
/// client only and never gates takeoff or influences the simulation (GitHub issue #230). The only writer
/// of <see cref="AircraftGroundOps.ReleaseWindowStartUtc"/>/<see cref="AircraftGroundOps.ReleaseWindowEndUtc"/>.
/// </summary>
public static class CfrDepartureService
{
    /// <summary>
    /// Clears the release window when <paramref name="cmd"/> is <see cref="CfrAction.Clear"/>, otherwise
    /// resolves and stores an absolute-UTC window from <paramref name="nowUtc"/> using the FAA-fixed CFR
    /// offsets. Returns the controller-facing echo describing the resulting window (or its removal).
    /// Only <see cref="CfrAction.Set"/>/<see cref="CfrAction.Clear"/> reach here; <see cref="CfrAction.Check"/>
    /// is a read-only query handled via <see cref="DescribeStatus"/>.
    /// </summary>
    public static string Apply(AircraftState aircraft, CfrDepartureCommand cmd, DateTime nowUtc)
    {
        if (cmd.Action == CfrAction.Clear)
        {
            aircraft.Ground.ReleaseWindowStartUtc = null;
            aircraft.Ground.ReleaseWindowEndUtc = null;
            return $"{aircraft.Callsign} release window cleared";
        }

        var window = CfrWindowResolver.Resolve(cmd.Hhmm, nowUtc);
        aircraft.Ground.ReleaseWindowStartUtc = window.StartUtc;
        aircraft.Ground.ReleaseWindowEndUtc = window.EndUtc;

        // "released for departure" is the FAA phrase (7110.65 §4-3-4.a/.c.3). A timed release shows the
        // assigned anchor alongside the −2/+1 bracket; a bare CFR is an immediate release ("now").
        var bracket = $"window {window.StartUtc:HHmm}–{window.EndUtc:HHmm}Z";
        if (cmd.Hhmm is null)
        {
            return $"{aircraft.Callsign} released for departure now ({bracket})";
        }

        var assigned = window.StartUtc.AddSeconds(CfrWindow.WindowBeforeSeconds);
        return $"{aircraft.Callsign} released for departure at {assigned:HHmm}Z ({bracket})";
    }

    /// <summary>
    /// Human-readable readout of an aircraft's current release-window status (<c>CFR CHECK</c>), reporting
    /// time to open / to close / since expiry against <paramref name="nowUtc"/>. Active only while the
    /// aircraft is on the ground — a departed aircraft (or one with no window) reads as inactive.
    /// </summary>
    public static string DescribeStatus(AircraftState aircraft, DateTime nowUtc)
    {
        if (!aircraft.IsOnGround || aircraft.Ground.ReleaseWindowStartUtc is not { } start || aircraft.Ground.ReleaseWindowEndUtc is not { } end)
        {
            return $"{aircraft.Callsign} has no active release window";
        }

        var remaining = CfrCountdown.Evaluate(new ReleaseWindow(start, end), nowUtc);
        var bracket = $"window {start:HHmm}–{end:HHmm}Z";
        return remaining.Phase switch
        {
            CfrPhase.BeforeOpen => $"{aircraft.Callsign} release {bracket} — opens in {FormatMinSec(remaining.Seconds)}",
            CfrPhase.Open => $"{aircraft.Callsign} release {bracket} — closes in {FormatMinSec(remaining.Seconds)}",
            _ => $"{aircraft.Callsign} release {bracket} — expired {FormatMinSec(remaining.Seconds)} ago",
        };
    }

    private static string FormatMinSec(int seconds) => $"{seconds / 60}:{seconds % 60:D2}";
}

using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a menu row shows on the radar views while it is pointed at or focused (<see cref="IMenuHost.SetRadarCue"/>): a solid
/// ring on the aircraft the row names, a dashed bearing line to it from the aircraft the menu commands, labelled with the
/// clock and miles between them, and a dashed ring on the aircraft the call resolves to when that is another one.
/// </summary>
/// <param name="FromCallsign">The aircraft the menu commands, where the bearing line starts.</param>
/// <param name="TargetCallsign">
/// The aircraft the row names: ringed solid, where the bearing line ends; null when no row is pointed at, which leaves the
/// resolved aircraft's dashed ring alone.
/// </param>
/// <param name="ResolvedCallsign">
/// The aircraft the call resolves to, ringed dashed when it is not <paramref name="TargetCallsign"/>; null for none.
/// </param>
/// <param name="ShowBearingLabel">Whether the bearing line carries its <c>{clock} o'clock · {nm} nm</c> label.</param>
public sealed record RadarCue(string FromCallsign, string? TargetCallsign, string? ResolvedCallsign, bool ShowBearingLabel)
{
    /// <summary>
    /// Where each end of the cue is among <paramref name="aircraft"/>, the aircraft a radar view draws: an end whose
    /// callsign is not among them draws nothing (no ring, and no bearing line when it is the target or the commanded
    /// aircraft). Null when neither ring has an aircraft to draw on.
    /// </summary>
    public ResolvedRadarCue? ResolveAgainst(IReadOnlyList<IMenuAircraft> aircraft)
    {
        IMenuAircraft? target = (TargetCallsign is null) ? null : Find(aircraft, TargetCallsign);
        IMenuAircraft? resolvedOther = ((ResolvedCallsign is { } resolved) && (resolved != TargetCallsign)) ? Find(aircraft, resolved) : null;
        if ((target is null) && (resolvedOther is null))
        {
            return null;
        }

        IMenuAircraft? from = ((target is not null) && (FromCallsign != TargetCallsign)) ? Find(aircraft, FromCallsign) : null;
        string? label = ((target is not null) && (from is not null) && ShowBearingLabel) ? BearingLabel(from, target) : null;
        return new ResolvedRadarCue(target?.Position, resolvedOther?.Position, from?.Position, label);
    }

    /// <summary>
    /// The bearing line's label: <c>{clock} o'clock · {nm} nm</c>, the clock and whole miles the traffic row gives for the
    /// pair (<see cref="RelativeGeometry.TrafficRow"/>).
    /// </summary>
    private static string BearingLabel(IMenuAircraft from, IMenuAircraft target)
    {
        MenuTrafficRow row = RelativeGeometry.TrafficRow(from, target);
        return $"{row.ClockPosition} o'clock · {RelativeGeometry.WholeNm(row.DistanceNm)} nm";
    }

    private static IMenuAircraft? Find(IReadOnlyList<IMenuAircraft> aircraft, string callsign) =>
        aircraft.FirstOrDefault(ac => string.Equals(ac.Callsign, callsign, StringComparison.Ordinal));
}

/// <summary>
/// A <see cref="RadarCue"/> with its ends placed, as a radar frame draws it: the solid ring at <paramref name="Target"/>, the
/// dashed ring at <paramref name="ResolvedOther"/>, the bearing line from <paramref name="From"/> to
/// <paramref name="Target"/> with <paramref name="BearingLabel"/> at its midpoint. A part left null is not drawn; the line
/// needs both of its ends.
/// </summary>
/// <param name="Target">Where the aircraft the row names is.</param>
/// <param name="ResolvedOther">Where the aircraft the call resolves to is, when it is another aircraft.</param>
/// <param name="From">Where the commanded aircraft is, when the bearing line is drawn.</param>
/// <param name="BearingLabel">The line's label, <c>{clock} o'clock · {nm} nm</c>; null for none.</param>
public sealed record ResolvedRadarCue(LatLon? Target, LatLon? ResolvedOther, LatLon? From, string? BearingLabel);

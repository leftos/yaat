using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope;

/// <summary>What a middle click on the Scope does.</summary>
internal enum ScopeMiddleClickAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Toggles the clicked block's highlight on this Scope.</summary>
    ToggleHighlight,

    /// <summary>Fills the clicked track into the entry and runs it.</summary>
    FillTrackAndExecute,

    /// <summary>Runs <c>QF {callsign}</c> through the entry.</summary>
    RunQuickFlight,

    /// <summary>Fills the clicked point into the entry and runs it.</summary>
    FillPointAndExecute,
}

/// <summary>
/// The row a middle click on the Scope takes. On a track the row is picked by the block's resolved kind
/// (<see cref="ScopeOwnerKind.Resolve"/>): an ERAM block fills the track into a non-empty preview and runs it, and its Field A
/// with an empty preview runs <c>QF</c>; a STARS or EuroScope block toggles its highlight whatever the preview holds (strict
/// CRC). On the map the row is picked by the entry layout: ERAM (and Mixed) fills the point into a non-empty preview and runs
/// it. The armed Rings tool takes every middle click for nothing.
/// </summary>
internal static class ScopeMiddleClick
{
    /// <summary>The row for a middle click on <paramref name="ac"/>'s target or block on a Scope that picked <paramref name="templateId"/>.</summary>
    public static ScopeMiddleClickAction OnTrack(string templateId, AircraftModel ac, bool onEramFieldA, bool hasText, bool ringsArmed)
    {
        if (ringsArmed)
        {
            return ScopeMiddleClickAction.None;
        }

        if (ScopeOwnerKind.Resolve(templateId, ac) != ScopeTemplates.EramId)
        {
            return ScopeMiddleClickAction.ToggleHighlight;
        }

        if (hasText)
        {
            return ScopeMiddleClickAction.FillTrackAndExecute;
        }

        return onEramFieldA ? ScopeMiddleClickAction.RunQuickFlight : ScopeMiddleClickAction.None;
    }

    /// <summary>The row for a middle click on the map, where no block or target is.</summary>
    public static ScopeMiddleClickAction OnMap(bool eramLayout, bool hasText, bool ringsArmed) =>
        (!ringsArmed && eramLayout && hasText) ? ScopeMiddleClickAction.FillPointAndExecute : ScopeMiddleClickAction.None;
}

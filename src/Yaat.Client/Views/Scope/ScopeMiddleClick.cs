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

    /// <summary>Does what a plain left click on the hit field or indicator cell does: runs its toggle or opens its menu.</summary>
    ClickField,
}

/// <summary>What a middle press on a track landed on.</summary>
/// <param name="OnEramFieldA">Whether the press is on the callsign field (Field A) of the track's block drawn with the ERAM template.</param>
/// <param name="Action">
/// The action of the HSF indicator or VCI cell under the press (picked first, as a left press does), else of the track's own
/// field under it; <see cref="ScopeFieldAction.None"/> outside any field with one.
/// </param>
public readonly record struct ScopeMiddleHit(bool OnEramFieldA, ScopeFieldAction Action);

/// <summary>
/// The row a middle click on the Scope takes. On a track the row is picked by the template the block draws with, which the
/// caller resolves (<see cref="ScopeOwnerKind.Resolve"/>): an ERAM block fills the track into a non-empty preview and runs it; with an empty
/// preview its Field A runs <c>QF</c>, and its HSF indicator, VCI cell or a field with an action does what a plain left click
/// does there; a STARS or EuroScope block toggles its highlight whatever the preview holds (strict CRC). On the map the row is
/// picked by the entry layout: ERAM (and Mixed) fills the point into a non-empty preview and runs it. The armed Rings tool
/// takes every middle click for nothing.
/// </summary>
internal static class ScopeMiddleClick
{
    /// <summary>
    /// The row for a middle click, landing on <paramref name="hit"/>, on a track's target or block that draws with
    /// <paramref name="trackTemplateId"/>, the template already resolved for that track.
    /// </summary>
    public static ScopeMiddleClickAction OnTrack(string trackTemplateId, ScopeMiddleHit hit, bool hasText, bool ringsArmed)
    {
        if (ringsArmed)
        {
            return ScopeMiddleClickAction.None;
        }

        if (trackTemplateId != ScopeTemplates.EramId)
        {
            return ScopeMiddleClickAction.ToggleHighlight;
        }

        if (hasText)
        {
            return ScopeMiddleClickAction.FillTrackAndExecute;
        }

        if (hit.OnEramFieldA)
        {
            return ScopeMiddleClickAction.RunQuickFlight;
        }

        return (hit.Action != ScopeFieldAction.None) ? ScopeMiddleClickAction.ClickField : ScopeMiddleClickAction.None;
    }

    /// <summary>The row for a middle click on the map, where no block or target is.</summary>
    public static ScopeMiddleClickAction OnMap(bool eramLayout, bool hasText, bool ringsArmed) =>
        (!ringsArmed && eramLayout && hasText) ? ScopeMiddleClickAction.FillPointAndExecute : ScopeMiddleClickAction.None;
}

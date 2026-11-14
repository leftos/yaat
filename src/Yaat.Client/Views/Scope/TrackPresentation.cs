namespace Yaat.Client.Views.Scope;

/// <summary>How much of a track's datablock is shown. Step 1 presents only <see cref="Full"/>.</summary>
public enum ScopeBlockLevel
{
    /// <summary>Limited datablock.</summary>
    Limited,

    /// <summary>Partial datablock.</summary>
    Partial,

    /// <summary>Full datablock.</summary>
    Full,

    /// <summary>Full datablock expanded by hover.</summary>
    Dwell,
}

/// <summary>A named colour role; the renderer maps each token to a colour and brightness group.</summary>
public enum ScopeColorToken
{
    /// <summary>The block's own colour.</summary>
    Default,

    /// <summary>The selected track's block.</summary>
    Selected,

    /// <summary>Instructor note (amber today).</summary>
    Note,

    /// <summary>No-landing-clearance warning (red today).</summary>
    NoLandingClearance,

    /// <summary>Conflict alert.</summary>
    ConflictAlert,

    /// <summary>ATPA in-trail distance in the warning cone state.</summary>
    AtpaWarning,

    /// <summary>ATPA in-trail distance in the alert cone state.</summary>
    AtpaAlert,
}

/// <summary>What clicking a field does. Every step-1 field carries <see cref="None"/>.</summary>
public enum ScopeFieldAction
{
    /// <summary>The field is inert.</summary>
    None,
}

/// <summary>
/// One datablock field as the presenter decided it.
/// </summary>
/// <param name="Slot">Where the field sits.</param>
/// <param name="Text">Text to draw this frame; empty during a flashing field's off-phase.</param>
/// <param name="StableText">Text the field shows on its on-phase, which a reserved slot measures so rects never pulse.</param>
/// <param name="Token">Colour role of the field.</param>
/// <param name="Action">What clicking the field does.</param>
public sealed record ScopeField(ScopeFieldSlot Slot, string Text, string StableText, ScopeColorToken Token, ScopeFieldAction Action);

/// <summary>
/// Everything a template needs to lay out one track's datablock, produced by <see cref="TrackPresenter.Present"/>.
/// </summary>
/// <param name="TemplateId">Id of the template the fields were presented for.</param>
/// <param name="Level">Block level.</param>
/// <param name="Token">Colour role of the block as a whole.</param>
/// <param name="Fields">Fields for every slot the template places, in template order.</param>
public sealed record TrackPresentation(string TemplateId, ScopeBlockLevel Level, ScopeColorToken Token, IReadOnlyList<ScopeField> Fields);

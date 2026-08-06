namespace Yaat.Client.ViewModels;

/// <summary>
/// Split layout of a strips tab/window. Named by resulting pane arrangement
/// rather than splitter orientation ("split horizontally" is ambiguous —
/// different apps mean opposite things by it).
/// </summary>
public enum StripsSplitMode
{
    /// <summary>Single pane — the default.</summary>
    None,

    /// <summary>Two panes left/right of a vertical splitter.</summary>
    SideBySide,

    /// <summary>Two panes above/below a horizontal splitter.</summary>
    Stacked,
}

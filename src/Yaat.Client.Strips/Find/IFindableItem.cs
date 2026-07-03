namespace Yaat.Client.Find;

/// <summary>
/// A row that the shared in-view Find (Ctrl+F) can search and highlight. Implemented by
/// <c>StripItemViewModel</c> (vStrips) and <c>TdlsItemViewModel</c> (vTDLS) so a single
/// <see cref="FindController"/> drives both views.
///
/// The two flags are observable properties on the implementing view-models — the XAML
/// binds a highlight overlay to them and <see cref="FindController"/> is their only writer.
/// </summary>
public interface IFindableItem
{
    /// <summary>All searchable text for this row (callsign + every visible field), space-joined.</summary>
    string GetFindText();

    /// <summary>True while this row is one of the current query's matches (subtle highlight).</summary>
    bool IsFindMatch { get; set; }

    /// <summary>True for the single match the view has scrolled to (strong highlight).</summary>
    bool IsCurrentFindMatch { get; set; }
}

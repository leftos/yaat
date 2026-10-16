using Avalonia.Input;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The scope feedback a flyout's rows drive: the row under the pointer, else the row holding keyboard focus, else none,
/// is previewed (<see cref="IMenuHost.SetScopePreview"/>, with the preview <paramref name="previewFor"/> builds for its key
/// at that moment, null to draw nothing) and, when <paramref name="highlightsAircraft"/>, its key is the callsign of an
/// aircraft that is highlighted (<see cref="IMenuHost.HighlightAircraft"/>); a Hold short of… stop's key is its command,
/// and it highlights no aircraft. The two rows are tracked apart, so the pointer leaving a row hands the feedback back to
/// the focused row, and focus passing through another row leaves the pointed row's feedback in place.
/// </summary>
/// <param name="host">The host the highlight and the preview go to.</param>
/// <param name="previewFor">The preview for a row's key; null draws nothing for it.</param>
/// <param name="highlightsAircraft">Whether a row's key is a callsign the host highlights.</param>
internal sealed class ScopeFeedbackTracker(IMenuHost host, Func<string, ScopePreview?> previewFor, bool highlightsAircraft)
{
    private string? _pointed;
    private string? _focused;

    /// <summary>Has <paramref name="row"/>, standing for <paramref name="key"/>, drive the feedback while pointed at or focused.</summary>
    public void Track(InputElement row, string key)
    {
        row.PointerEntered += (_, _) => Point(key);
        row.PointerExited += (_, _) => Unpoint(key);
        // A menu row marks its focus events handled before handlers added the plain way would see them.
        row.AddHandler(InputElement.GotFocusEvent, (_, _) => Focus(key), handledEventsToo: true);
        row.AddHandler(InputElement.LostFocusEvent, (_, _) => Blur(key), handledEventsToo: true);
    }

    /// <summary>Forgets the pointed and the focused row and clears the feedback, as the flyout closes.</summary>
    public void Clear()
    {
        _pointed = null;
        _focused = null;
        Apply();
    }

    /// <summary>
    /// Stops the feedback for <paramref name="key"/> when it is the pointed row, for a row going out of reach, which
    /// raises no PointerExited of its own.
    /// </summary>
    public void Unpoint(string key)
    {
        if (_pointed == key)
        {
            _pointed = null;
            Apply();
        }
    }

    /// <summary>
    /// Sends the shown row's preview again, as its list refills, so its routes and meet point follow the traffic; the
    /// highlight is left as it is.
    /// </summary>
    public void Refresh() => host.SetScopePreview(Preview());

    private void Point(string key)
    {
        _pointed = key;
        Apply();
    }

    private void Focus(string key)
    {
        _focused = key;
        Apply();
    }

    private void Blur(string key)
    {
        if (_focused == key)
        {
            _focused = null;
        }

        Apply();
    }

    private void Apply()
    {
        if (highlightsAircraft)
        {
            host.HighlightAircraft(_pointed ?? _focused);
        }

        host.SetScopePreview(Preview());
    }

    private ScopePreview? Preview() => ((_pointed ?? _focused) is { } shown) ? previewFor(shown) : null;
}

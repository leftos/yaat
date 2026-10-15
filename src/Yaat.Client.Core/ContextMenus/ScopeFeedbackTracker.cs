using Avalonia.Input;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The scope feedback a flyout's rows drive: the aircraft of the row under the pointer, else the one of the row holding
/// keyboard focus, else none, is highlighted (<see cref="IMenuHost.HighlightAircraft"/>) and previewed
/// (<see cref="IMenuHost.SetScopePreview"/>, with the preview <paramref name="previewFor"/> builds for its callsign at that
/// moment, null to draw nothing). The two rows are tracked apart, so the pointer leaving a row hands the feedback back to
/// the focused row, and focus passing through another row leaves the pointed row's feedback in place.
/// </summary>
/// <param name="host">The host the highlight and the preview go to.</param>
/// <param name="previewFor">The preview for a row's callsign; null draws nothing for it.</param>
internal sealed class ScopeFeedbackTracker(IMenuHost host, Func<string, ScopePreview?> previewFor)
{
    private string? _pointed;
    private string? _focused;

    /// <summary>Has <paramref name="row"/>, standing for <paramref name="callsign"/>, drive the feedback while pointed at or focused.</summary>
    public void Track(InputElement row, string callsign)
    {
        row.PointerEntered += (_, _) => Point(callsign);
        row.PointerExited += (_, _) => Unpoint(callsign);
        // A menu row marks its focus events handled before handlers added the plain way would see them.
        row.AddHandler(InputElement.GotFocusEvent, (_, _) => Focus(callsign), handledEventsToo: true);
        row.AddHandler(InputElement.LostFocusEvent, (_, _) => Blur(callsign), handledEventsToo: true);
    }

    /// <summary>Forgets the pointed and the focused row and clears the feedback, as the flyout closes.</summary>
    public void Clear()
    {
        _pointed = null;
        _focused = null;
        Apply();
    }

    /// <summary>
    /// Stops the feedback for <paramref name="callsign"/> when it is the pointed row, for a row going out of reach, which
    /// raises no PointerExited of its own.
    /// </summary>
    public void Unpoint(string callsign)
    {
        if (_pointed == callsign)
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

    private void Point(string callsign)
    {
        _pointed = callsign;
        Apply();
    }

    private void Focus(string callsign)
    {
        _focused = callsign;
        Apply();
    }

    private void Blur(string callsign)
    {
        if (_focused == callsign)
        {
            _focused = null;
        }

        Apply();
    }

    private void Apply()
    {
        host.HighlightAircraft(_pointed ?? _focused);
        host.SetScopePreview(Preview());
    }

    private ScopePreview? Preview() => ((_pointed ?? _focused) is { } shown) ? previewFor(shown) : null;
}

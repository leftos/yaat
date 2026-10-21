using Avalonia.Input;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The radar cue a flyout's traffic rows drive: the row under the pointer, else the row holding keyboard focus, else none,
/// has its cue shown (<see cref="IMenuHost.SetRadarCue"/>, with the cue <paramref name="cueFor"/> builds for its key, null
/// for none) and the aircraft its cue rings solid highlighted (<see cref="IMenuHost.HighlightAircraft"/>). The two rows are
/// tracked apart, as <see cref="ScopeFeedbackTracker"/> tracks them: the pointer leaving a row hands the cue back to the
/// focused row, and focus passing through another row leaves the pointed row's cue in place.
/// </summary>
/// <param name="host">The host the cue and the highlight go to.</param>
/// <param name="cueFor">The cue for a row's key; null shows none for it.</param>
internal sealed class RadarCueTracker(IMenuHost host, Func<string, RadarCue?> cueFor)
{
    private string? _pointed;
    private string? _focused;

    /// <summary>Has <paramref name="row"/>, standing for <paramref name="key"/>, drive the cue while pointed at or focused.</summary>
    public void Track(InputElement row, string key)
    {
        row.PointerEntered += (_, _) => Point(key);
        row.PointerExited += (_, _) => Unpoint(key);
        // A menu row marks its focus events handled before handlers added the plain way would see them.
        row.AddHandler(InputElement.GotFocusEvent, (_, _) => Focus(key), handledEventsToo: true);
        row.AddHandler(InputElement.LostFocusEvent, (_, _) => Blur(key), handledEventsToo: true);
    }

    /// <summary>Forgets the pointed and the focused row and clears the cue and the highlight, as the flyout closes.</summary>
    public void Clear()
    {
        _pointed = null;
        _focused = null;
        Apply();
    }

    private void Point(string key)
    {
        _pointed = key;
        Apply();
    }

    private void Unpoint(string key)
    {
        if (_pointed == key)
        {
            _pointed = null;
            Apply();
        }
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
        RadarCue? cue = ((_pointed ?? _focused) is { } shown) ? cueFor(shown) : null;
        host.HighlightAircraft(cue?.TargetCallsign);
        host.SetRadarCue(cue);
    }
}

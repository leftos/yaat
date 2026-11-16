using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The per-track PTLs: the callsigns whose track draws a vector whatever the global VECTOR setting says, toggled by
/// <c>.scope ptl</c> and a STARS <c>MF R</c> entry. This Scope's own overlay: never saved, never sent to the server, and
/// cleared when their aircraft leaves and on a scenario unload, restart or rewind.
/// </summary>
public sealed partial class ScopeViewModel
{
    internal static readonly IReadOnlySet<string> NoPtl = new HashSet<string>();

    private readonly HashSet<string> _ptl = new(StringComparer.Ordinal);

    /// <summary>The callsigns with a PTL, a snapshot replaced on every change; the canvas draws their vectors from it.</summary>
    [ObservableProperty]
    private IReadOnlySet<string> _localPtlCallsigns = NoPtl;

    /// <summary>Toggles the PTL on the track with <paramref name="callsign"/>; true when it is now on.</summary>
    public bool TogglePtl(string callsign)
    {
        bool on = !_ptl.Remove(callsign);
        if (on)
        {
            _ptl.Add(callsign);
        }

        PublishPtl();
        return on;
    }

    /// <summary>Removes the PTL on the track with <paramref name="callsign"/>.</summary>
    public void ClearPtl(string callsign)
    {
        if (_ptl.Remove(callsign))
        {
            PublishPtl();
        }
    }

    /// <summary>Removes every PTL.</summary>
    public void ClearPtl()
    {
        if (_ptl.Count > 0)
        {
            _ptl.Clear();
            PublishPtl();
        }
    }

    private void PublishPtl() => LocalPtlCallsigns = new HashSet<string>(_ptl, StringComparer.Ordinal);
}

using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The pilot lens: overlays drawn from simulation truth, switched by <c>.scope lens</c> and the PILOT LENS menu. Its
/// switches (<see cref="Lens"/>) are display state, kept in pref sets and the saved state; the tracks whose intent path is
/// toggled on are this Scope's own, never saved, and dropped when their aircraft leaves and on a scenario unload, restart
/// or rewind. Nothing here is sent to the server.
/// </summary>
public sealed partial class ScopeViewModel
{
    internal static readonly IReadOnlySet<string> NoLensPaths = new HashSet<string>();

    private readonly HashSet<string> _lensPaths = new(StringComparer.Ordinal);

    /// <summary>The lens's switches; all off on a fresh scope.</summary>
    [ObservableProperty]
    private ScopeLensSettings _lens = ScopeLensSettings.Off;

    /// <summary>
    /// The callsigns whose intent path is toggled on, a snapshot replaced on every change; with the lens and its path on,
    /// the canvas draws their paths and the selected aircraft's.
    /// </summary>
    [ObservableProperty]
    private IReadOnlySet<string> _lensPathCallsigns = NoLensPaths;

    /// <summary>Whether <paramref name="item"/> names a lens overlay <c>.scope lens</c> takes, case-sensitive lowercase.</summary>
    internal static bool IsLensOverlay(string item) => item is ScopeToolbarButtons.LensPathItem or ScopeToolbarButtons.LensQueueItem;

    /// <summary>Turns the lens's master switch on (<paramref name="on"/>) or off.</summary>
    public void SetLensEnabled(bool on) => Lens = Lens with { Enabled = on };

    /// <summary>Turns the lens overlay <paramref name="item"/> on (<paramref name="on"/>) or off; an unknown item changes nothing.</summary>
    public void SetLensOverlay(string item, bool on)
    {
        if (item == ScopeToolbarButtons.LensPathItem)
        {
            Lens = Lens with { Path = on };
        }
        else if (item == ScopeToolbarButtons.LensQueueItem)
        {
            Lens = Lens with { Queue = on };
        }
    }

    /// <summary>Toggles the intent path of the track with <paramref name="callsign"/>; true when it is now on.</summary>
    public bool ToggleLensPath(string callsign)
    {
        bool on = !_lensPaths.Remove(callsign);
        if (on)
        {
            _lensPaths.Add(callsign);
        }

        PublishLensPaths();
        return on;
    }

    /// <summary>Drops the toggled intent path of the track with <paramref name="callsign"/>, as when its aircraft leaves.</summary>
    public void ClearLensPath(string callsign)
    {
        if (_lensPaths.Remove(callsign))
        {
            PublishLensPaths();
        }
    }

    /// <summary>Drops every toggled intent path.</summary>
    public void ClearLensPaths()
    {
        if (_lensPaths.Count > 0)
        {
            _lensPaths.Clear();
            PublishLensPaths();
        }
    }

    /// <summary>
    /// Whether the lens toggle <paramref name="id"/> is on: ON while the lens is, PATH while its path is, QUEUE while its queue
    /// is; else false.
    /// </summary>
    private bool IsLensToggleOn(ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.LensOn => Lens.Enabled,
            ScopeButtonId.LensPath => Lens.Path,
            ScopeButtonId.LensQueue => Lens.Queue,
            _ => false,
        };

    private void PublishLensPaths() => LensPathCallsigns = new HashSet<string>(_lensPaths, StringComparer.Ordinal);
}

/// <summary>The pilot lens's switches. Each overlay draws only while <see cref="Enabled"/> and its own switch are both on.</summary>
/// <param name="Enabled">PILOT LENS: the master switch.</param>
/// <param name="Path">PATH: the intent path, drawn for the selected aircraft and every toggled track.</param>
/// <param name="Queue">QUEUE: the queued-commands badge, drawn on every track with a pending command.</param>
public sealed record ScopeLensSettings(bool Enabled, bool Path, bool Queue)
{
    /// <summary>Every switch off: what a fresh scope and a pref set with no lens hold.</summary>
    public static ScopeLensSettings Off { get; } = new(false, false, false);

    /// <summary>Whether the intent path draws: the lens and its path both on.</summary>
    public bool DrawsPath => Enabled && Path;

    /// <summary>Whether the queued-commands badge draws: the lens and its queue both on.</summary>
    public bool DrawsQueue => Enabled && Queue;
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One of the Scope's own video-map toggles: a copy of a Radar map toggle's data, taken once when the Scope is seeded, with
/// the Scope's own on/off state. Turning it on or off never touches the Radar's toggle.
/// </summary>
public sealed partial class ScopeMapToggle : ObservableObject
{
    /// <summary>The video map's id, as the video-map service caches it.</summary>
    public required string MapId { get; init; }

    /// <summary>The map's STARS id: <c>.scope map</c> and the MAPS tray name the map by it.</summary>
    public required int StarsId { get; init; }

    /// <summary>The map's short name.</summary>
    public required string ShortName { get; init; }

    /// <summary>The map's full name.</summary>
    public required string Name { get; init; }

    /// <summary>The map's brightness category ("A" or "B").</summary>
    public required string BrightnessCategory { get; init; }

    /// <summary>Whether the Scope draws the map.</summary>
    [ObservableProperty]
    private bool _isOn;
}

using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's display state: its BRITE groups. Changed only by <c>.scope brite</c> (the BRITE spinners run it); never
/// written to the preferences, never sent to the server.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>The scope's BRITE groups, opening at <see cref="ScopeBrightness.Default"/>.</summary>
    [ObservableProperty]
    private ScopeBrightness _brightness = ScopeBrightness.Default;

    /// <summary>
    /// Sets the BRITE group named <paramref name="group"/> (case-insensitive) to <paramref name="value"/>, as
    /// <c>.scope brite</c> does once it has checked both.
    /// </summary>
    /// <exception cref="ArgumentException">The group is unknown, or the value is outside its range.</exception>
    internal void SetBrightness(string group, int value)
    {
        ScopeBrightnessGroup found = ScopeBrightness.FindGroup(group) ?? throw new ArgumentException($"Unknown BRITE group: {group}", nameof(group));
        if (!found.Contains(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Outside {found.Name}'s {found.Min} to {found.Max}");
        }

        Brightness = found.With(Brightness, value);
    }
}

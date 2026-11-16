using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's zoom bands: the profile's band the range selects, with the values pinned by hand laid over it
/// (<see cref="EffectiveBand"/>). A hand change pins its value until AUTO clears every pin; range never pins, a profile
/// change keeps the pins, and pref sets and the saved state store the pinned values only.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>The values pinned by hand; <see cref="ScopeBandPins.None"/> when every value follows the band.</summary>
    [ObservableProperty]
    private ScopeBandPins _pins = ScopeBandPins.None;

    /// <summary>
    /// The values in effect: the profile's band for <see cref="RangeNm"/> with <see cref="Pins"/> over it, recomputed when the
    /// range, the profile or a pin changes. The canvas draws others' tracks at its <see cref="ScopeZoomBand.OthersLevel"/>.
    /// </summary>
    [ObservableProperty]
    private ScopeZoomBand _effectiveBand = ScopeZoomBands.Resolve(ScopeProfileStore.Shipped(ScopeProfileKind.Mixed).Bands, DefaultRangeNm);

    /// <summary>Whether <paramref name="word"/> is an others' level word: <c>full</c> or <c>partial</c>.</summary>
    internal static bool IsOthersLevel(string word) => word is ScopeProfile.OthersFull or ScopeProfile.OthersPartial;

    /// <summary>Pins others' block level to <paramref name="level"/> (<c>full</c> or <c>partial</c>), as <c>.scope others</c> does.</summary>
    /// <exception cref="ArgumentException">The level is no others' level word.</exception>
    internal void PinOthersLevel(string level) =>
        Pins = IsOthersLevel(level)
            ? Pins with
            {
                OthersLevel = level,
            }
            : throw new ArgumentException($"Unknown others' level '{level}'.", nameof(level));

    /// <summary>Clears every pin, as <c>.scope auto</c> does, so every value follows the band again.</summary>
    internal void ClearPins() => Pins = ScopeBandPins.None;

    /// <summary>The others' level the OTHERS button flips to: the other word from the one in effect.</summary>
    internal string FlippedOthersLevel() =>
        EffectiveBand.OthersLevel == ScopeProfile.OthersFull ? ScopeProfile.OthersPartial : ScopeProfile.OthersFull;

    partial void OnPinsChanged(ScopeBandPins value) => RecomputeBand();

    partial void OnRangeNmChanged(double value) => RecomputeBand();

    /// <summary>Recomputes <see cref="EffectiveBand"/> from the profile's bands, the range and the pins.</summary>
    private void RecomputeBand()
    {
        ScopeZoomBand effective = ScopeZoomBands.Effective(ScopeZoomBands.Resolve(Profile.Bands, RangeNm), Pins);
        if (effective != EffectiveBand)
        {
            EffectiveBand = effective;
        }
    }

    /// <summary>
    /// Applies a pref set's pins: a null field leaves that value to the band, and a value the scope does not take keeps
    /// the current pin with a warning.
    /// </summary>
    private void ApplyPins(ScopePrefSet set)
    {
        ScopeBandPins kept = Pins;
        Pins = new ScopeBandPins(
            PinOrKept(set.PinOthersLevel, IsOthersLevel, kept.OthersLevel, "others' level"),
            PinOrKept(set.PinVectorMin, minutes => minutes >= 0, kept.VectorMin, "vector minutes"),
            PinOrKept(set.PinHistory, count => count >= 0, kept.History, "history count"),
            PinOrKept(set.PinRings, word => word is ScopeProfile.RingsJRing or ScopeProfile.RingsDri, kept.Rings, "ring kind")
        );
    }

    private static string? PinOrKept(string? value, Func<string, bool> isValid, string? kept, string what)
    {
        if ((value is null) || isValid(value))
        {
            return value;
        }

        Log.LogWarning("A pref set pins {What} '{Value}', which the scope does not take; keeping {Current}", what, value, kept ?? "the band's");
        return kept;
    }

    private static T? PinOrKept<T>(T? value, Func<T, bool> isValid, T? kept, string what)
        where T : struct
    {
        if ((value is not { } given) || isValid(given))
        {
            return value;
        }

        Log.LogWarning(
            "A pref set pins {What} {Value}, which the scope does not take; keeping {Current}",
            what,
            given,
            (object?)kept ?? "the band's"
        );
        return kept;
    }
}

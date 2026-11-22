using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

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

    /// <summary>
    /// The template <paramref name="ac"/> draws with: its owner's kind on Mixed, an unowned track's the band's
    /// <see cref="ScopeZoomBand.Unowned"/>, else the picked template.
    /// </summary>
    internal string TrackTemplateId(AircraftModel ac) => ScopeOwnerKind.Resolve(TemplateId, ac, EffectiveBand.Unowned);

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

    /// <summary>
    /// The vector lengths, in minutes, the VECTOR spinner and <c>.scope vector</c> take, ascending; 0 draws no line.
    /// </summary>
    internal static IReadOnlyList<double> VectorMinutes { get; } = [0, 0.5, 1, 1.5, 2, 2.5, 3, 4, 8];

    /// <summary>The most history dots HIST takes: every sample the server keeps.</summary>
    internal const int MaxHistory = AircraftState.PositionHistoryCapacity;

    /// <summary>
    /// The tracks vector lines draw on: the viewer's own (every track with no viewer) or all. A setting, neither banded nor
    /// pinned, so AUTO leaves it.
    /// </summary>
    [ObservableProperty]
    private ScopeVectorOwnership _vectorOwnership = ScopeVectorOwnership.Own;

    /// <summary>Whether <paramref name="minutes"/> is one of <see cref="VectorMinutes"/>.</summary>
    internal static bool IsVectorMinutes(double minutes) => VectorMinutes.Contains(minutes);

    /// <summary>Whether <paramref name="count"/> is a history dot count, 0 to <see cref="MaxHistory"/>.</summary>
    internal static bool IsHistoryCount(int count) => count is >= 0 and <= MaxHistory;

    /// <summary>Pins the vector length to <paramref name="minutes"/>, as <c>.scope vector</c> does.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The minutes are none of <see cref="VectorMinutes"/>.</exception>
    internal void PinVectorMin(double minutes) =>
        Pins = IsVectorMinutes(minutes)
            ? Pins with
            {
                VectorMin = minutes,
            }
            : throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "Vector minutes are 0, 0.5, 1, 1.5, 2, 2.5, 3, 4 or 8.");

    /// <summary>Pins the history dot count to <paramref name="count"/>, as <c>.scope hist</c> does.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is outside 0 to <see cref="MaxHistory"/>.</exception>
    internal void PinHistory(int count) =>
        Pins = IsHistoryCount(count)
            ? Pins with
            {
                History = count,
            }
            : throw new ArgumentOutOfRangeException(nameof(count), count, $"History counts are 0 to {MaxHistory}.");

    /// <summary>The OWN or ALL a VECTOR click flips to: the other of the current one.</summary>
    internal ScopeVectorOwnership FlippedVectorOwnership() =>
        (VectorOwnership == ScopeVectorOwnership.All) ? ScopeVectorOwnership.Own : ScopeVectorOwnership.All;

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
            PinOrKept(set.PinVectorMin, IsVectorMinutes, kept.VectorMin, "vector minutes"),
            PinOrKept(set.PinHistory, IsHistoryCount, kept.History, "history count"),
            PinOrKept(set.PinRings, word => word is ScopeProfile.RingsJRing or ScopeProfile.RingsDri, kept.Rings, "ring kind")
        );
    }

    /// <summary>
    /// Applies a pref set's OWN or ALL; a word the scope does not take keeps the current setting with a warning.
    /// </summary>
    private void ApplyVectorOwnership(string word)
    {
        if (ScopeVectorOwnerships.TryParse(word, out ScopeVectorOwnership ownership))
        {
            VectorOwnership = ownership;
            return;
        }

        Log.LogWarning(
            "A pref set draws vectors on '{Value}', which the scope does not take; keeping {Current}",
            word,
            ScopeVectorOwnerships.NameOf(VectorOwnership)
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

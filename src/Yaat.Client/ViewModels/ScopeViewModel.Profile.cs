using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's profile: the room's profile (<see cref="MainViewModel.ScopeProfile"/>), applied to this scope whenever it
/// changes. Applying one orders the toolbar's buttons and the views by the profile's lists; the profile alone picks the
/// datablock look (<see cref="TemplateId"/>). Until the room has a profile the scope runs the STARS profile.
/// </summary>
public sealed partial class ScopeViewModel
{
    // The toolbar name of the TEMPLATE button the profile replaced; a profile's toolbar list naming it is not warned about.
    private const string RetiredTemplateButton = "template";

    /// <summary>The kind of the profile the scope shows; the toolbar's PROFILE button names it.</summary>
    [ObservableProperty]
    private ScopeProfileKind _profileKind = ScopeProfileKind.Stars;

    /// <summary>The bar the scope shows, picked from <see cref="ProfileKind"/>.</summary>
    [ObservableProperty]
    private ScopeBarKind _barKind = ScopeBarKind.Stars;

    /// <summary>The bar's top-level buttons, left to right, in the order the profile gives them; every button is on it.</summary>
    [ObservableProperty]
    private IReadOnlyList<ScopeButton> _toolbarButtons = ScopeToolbarButtons.All;

    /// <summary>The profile the scope shows, as its file holds it.</summary>
    internal ScopeProfile Profile { get; private set; } = ScopeProfileStore.Shipped(ScopeProfileKind.Stars);

    /// <summary>The store the scope reads the profile files from, or null to use the shipped profiles.</summary>
    internal ScopeProfileStore? ProfileStore { get; }

    /// <summary>
    /// Applies a pref set's profile name <paramref name="kind"/>, as <c>.scope pref load</c> does: a known name switches the
    /// room's profile, saved for no position type; an unknown one is kept as it is with a warning.
    /// </summary>
    /// <returns>The note the load's echo adds when the profile changed, else empty.</returns>
    internal string ApplyPrefSetProfile(string kind)
    {
        if (!ScopeProfilePicker.TryParseKind(kind, out ScopeProfileKind parsed))
        {
            Log.LogWarning("A pref set names unknown profile '{Kind}'; keeping {Current}", kind, ProfileKind);
            return "";
        }

        if (parsed == Main.ScopeProfile)
        {
            return "";
        }

        Main.SetScopeProfileFromPrefSet(parsed);
        return $"; {MainViewModel.ScopeProfileEcho(parsed)}";
    }

    /// <summary>
    /// The constructor's last step: the room's profile when it has one, else the STARS profile (its order, bands and bar)
    /// without writing it to the room; either way <see cref="TemplateId"/> is announced.
    /// </summary>
    private void InitProfile()
    {
        ScopeProfileKind kind = Main.ScopeProfile ?? ScopeProfileKind.Stars;
        ApplyProfileOrder(ProfileFor(kind), kind);
        OnPropertyChanged(nameof(TemplateId));
    }

    /// <summary>Follows the room's profile; a cleared one brings back the STARS profile.</summary>
    private void OnMainScopeProfileChanged() => InitProfile();

    // The single-argument OnProfileKindChanged is ScopeViewModel.Measure's; this overload announces the derived look.
    partial void OnProfileKindChanged(ScopeProfileKind oldValue, ScopeProfileKind newValue) => OnPropertyChanged(nameof(TemplateId));

    private ScopeProfile ProfileFor(ScopeProfileKind kind) => ProfileStore?.Get(kind) ?? ScopeProfileStore.Shipped(kind);

    /// <summary>
    /// Makes <paramref name="profile"/> the shown one and orders the bar and the views by it, with one warning for the names
    /// it gives that are none. The retired TEMPLATE button's name, which an old copy of a profile file's toolbar list may
    /// still hold, is skipped there without a warning; a view of that name is warned of like any other.
    /// </summary>
    private void ApplyProfileOrder(ScopeProfile profile, ScopeProfileKind kind)
    {
        Profile = profile;
        ProfileKind = kind;
        ScopeBarKind barKind = ScopeBarPicker.KindFor(kind);

        // A tray left open on the bar being replaced (TOOLS arms the ring tool) closes with it.
        if (barKind != BarKind)
        {
            OpenMenu = null;
        }

        BarKind = barKind;
        RecomputeBand();
        List<string> unknown = [];
        IReadOnlyList<ScopeButton> buttons = ScopeProfilePicker.Order(ScopeToolbarButtons.All, profile.Toolbar, ScopeToolbarButtons.NameOf, unknown);
        unknown.RemoveAll(name => string.Equals(name, RetiredTemplateButton, StringComparison.OrdinalIgnoreCase));
        if (!buttons.SequenceEqual(ToolbarButtons))
        {
            ToolbarButtons = buttons;
        }

        OrderViews([.. ScopeProfilePicker.Order(_allViews, profile.Views, view => view.Name, unknown)]);
        if (unknown.Count > 0)
        {
            Log.LogWarning(
                "Scope profile '{Profile}' names unknown buttons or views {Names}; skipping them",
                profile.Name,
                string.Join(", ", unknown)
            );
        }
    }

    /// <summary>Makes <paramref name="ordered"/> the views' dock order, re-sorting the open ones.</summary>
    private void OrderViews(ScopeDockViewModel[] ordered)
    {
        if (ordered.SequenceEqual(_allViews))
        {
            return;
        }

        _allViews = ordered;
        _openViews.Sort((a, b) => Array.IndexOf(_allViews, a).CompareTo(Array.IndexOf(_allViews, b)));
        SyncViewCollections();
    }
}

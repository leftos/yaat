using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's profile: the room's profile (<see cref="MainViewModel.ScopeProfile"/>), applied to this scope whenever it
/// changes. Applying one orders the toolbar's buttons and the views by the profile's lists, and picks its template unless
/// the viewer's facility has a saved state, whose template stands. Until the room has a profile the scope shows Mixed's
/// order and keeps the template it opened with.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>The kind of the profile the scope shows; the toolbar's PROFILE button names it.</summary>
    [ObservableProperty]
    private ScopeProfileKind _profileKind = ScopeProfileKind.Mixed;

    /// <summary>The bar the scope shows, picked from <see cref="ProfileKind"/>; a template pick never changes it.</summary>
    [ObservableProperty]
    private ScopeBarKind _barKind = ScopeBarKind.Classic;

    /// <summary>The bar's top-level buttons, left to right, in the order the profile gives them; every button is on it.</summary>
    [ObservableProperty]
    private IReadOnlyList<ScopeButton> _toolbarButtons = ScopeToolbarButtons.All;

    /// <summary>The profile the scope shows, as its file holds it.</summary>
    internal ScopeProfile Profile { get; private set; } = ScopeProfileStore.Shipped(ScopeProfileKind.Mixed);

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

    /// <summary>The constructor's last step: the room's profile when it has one, else Mixed's order with the opening template kept.</summary>
    private void InitProfile()
    {
        if (Main.ScopeProfile is { } kind)
        {
            ApplyProfile(kind);
            return;
        }

        ApplyProfileOrder(ProfileFor(ScopeProfileKind.Mixed), ScopeProfileKind.Mixed);
    }

    /// <summary>Follows the room's profile; a cleared one brings back Mixed's order, with the template kept.</summary>
    private void OnMainScopeProfileChanged() => InitProfile();

    /// <summary>
    /// Applies the profile of <paramref name="kind"/>: its toolbar and views order, and its template unless the viewer's
    /// facility has a saved state.
    /// </summary>
    private void ApplyProfile(ScopeProfileKind kind)
    {
        ScopeProfile profile = ProfileFor(kind);
        ApplyProfileOrder(profile, kind);
        if (HasSavedState())
        {
            return;
        }

        if (!ScopeTemplates.IsSelectable(profile.Template))
        {
            Log.LogWarning(
                "Scope profile '{Profile}' names unknown template '{TemplateId}'; keeping {Current}",
                profile.Name,
                profile.Template,
                TemplateId
            );
            return;
        }

        PickTemplateCommand.Execute(profile.Template);
    }

    private ScopeProfile ProfileFor(ScopeProfileKind kind) => ProfileStore?.Get(kind) ?? ScopeProfileStore.Shipped(kind);

    /// <summary>
    /// Makes <paramref name="profile"/> the shown one and orders the bar and the views by it, with one warning for the names
    /// it gives that are none.
    /// </summary>
    private void ApplyProfileOrder(ScopeProfile profile, ScopeProfileKind kind)
    {
        Profile = profile;
        ProfileKind = kind;
        BarKind = ScopeBarPicker.KindFor(kind);
        RecomputeBand();
        List<string> unknown = [];
        IReadOnlyList<ScopeButton> buttons = ScopeProfilePicker.Order(
            ScopeToolbarButtons.All,
            profile.Toolbar,
            button => ScopeToolbarButtons.NameOf(ScopeToolbarButtons.KeyOf(button)),
            unknown
        );
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

    /// <summary>Whether the viewer's facility has a saved state, whose template a profile leaves alone.</summary>
    private bool HasSavedState() =>
        (PrefStore is { } store) && (SafeFacility(Viewer.EramFacilityId) is { } facility) && (store.LoadCurrent(facility, Ordinal) is not null);
}

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's views: the SSA and the LA/CA/MCI list. An open view sits in the views dock, in a fixed order (SSA, then
/// LA/CA/MCI), or floats on the scope at its place (<see cref="ScopeDockViewModel.Place"/>); the SSA opens floating at the
/// scope's top-left. Every writer of a view's state (<c>.view</c>, the toolbar's VIEWS toggles, a card's close and dock
/// buttons, a card's move, a pref-set load) goes through <see cref="SetViewOpen"/>, <see cref="SetViewPlace"/>,
/// <see cref="ApplyOpenViews"/> or <see cref="RestoreViewPlace"/>.
/// </summary>
public sealed partial class ScopeViewModel
{
    private SsaViewModel _ssa;

    /// <summary>Every view, in dock order.</summary>
    private ScopeDockViewModel[] _allViews;

    /// <summary>The open views, docked or floating, in dock order: the single home of which views are open.</summary>
    private readonly List<ScopeDockViewModel> _openViews = [];

    /// <summary>The open views that sit in the dock, in dock order; the dock shows one card per item.</summary>
    public ObservableCollection<ScopeDockViewModel> DockViews { get; } = [];

    /// <summary>The open views that float on the scope, in dock order; the tear-off layer shows one card per item.</summary>
    public ObservableCollection<ScopeDockViewModel> FloatingViews { get; } = [];

    /// <summary>Whether any open view sits in the dock; the dock is hidden otherwise.</summary>
    public bool HasDockedViews => DockViews.Count > 0;

    /// <summary>The open views' names, docked or floating, in dock order.</summary>
    public IReadOnlyList<string> OpenViews => [.. _openViews.Select(v => v.Name)];

    /// <summary>Whether the SSA's 1 s clock is running. Test seam.</summary>
    internal bool IsClockRunning => _ssa.IsClockRunning;

    /// <summary>Whether <paramref name="name"/> names a view (<c>ssa</c> or <c>ca</c>; lowercase).</summary>
    public static bool IsViewName(string name) => name is ScopeToolbarButtons.ViewSsaName or ScopeToolbarButtons.ViewCaName;

    /// <summary>Whether the view named <paramref name="name"/> is open.</summary>
    public bool IsViewOpen(string name) => _openViews.Any(v => v.Name == name);

    /// <summary>
    /// Opens or closes the view named <paramref name="name"/>, at the place it has; setting the state it has changes nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The name is no view (<see cref="IsViewName"/>).</exception>
    public void SetViewOpen(string name, bool open)
    {
        ScopeDockViewModel view = ViewNamed(name);
        if (_openViews.Contains(view) == open)
        {
            return;
        }

        if (open)
        {
            Open(view);
        }
        else
        {
            view.StopReading();
            _openViews.Remove(view);
        }

        SyncViewCollections();
    }

    /// <summary>
    /// Opens the view named <paramref name="name"/> if it is closed, then floats it at <paramref name="place"/>, or docks it
    /// when <paramref name="place"/> is null.
    /// </summary>
    /// <exception cref="ArgumentException">The name is no view (<see cref="IsViewName"/>).</exception>
    public void SetViewPlace(string name, ScopePlacement? place)
    {
        ScopeDockViewModel view = ViewNamed(name);
        if (!_openViews.Contains(view))
        {
            Open(view);
        }

        view.Place = place;
        SyncViewCollections();
    }

    /// <summary>Opens exactly the views named in <paramref name="names"/> and closes the others; an unknown name is ignored.</summary>
    public void ApplyOpenViews(IEnumerable<string> names)
    {
        HashSet<string> wanted = new(names, StringComparer.Ordinal);
        foreach (ScopeDockViewModel view in _allViews)
        {
            SetViewOpen(view.Name, wanted.Contains(view.Name));
        }
    }

    /// <summary>
    /// Gives the view named <paramref name="name"/> <paramref name="place"/> without opening it: a closed view keeps the
    /// place it floats at when it opens, and a null place docks it.
    /// </summary>
    /// <exception cref="ArgumentException">The name is no view (<see cref="IsViewName"/>).</exception>
    internal void RestoreViewPlace(string name, ScopePlacement? place)
    {
        ViewNamed(name).Place = place;
        SyncViewCollections();
    }

    [MemberNotNull(nameof(_ssa), nameof(_allViews))]
    private void InitViews()
    {
        _ssa = new SsaViewModel(this) { Place = new ScopePlacement(ScopeCorner.TopLeft, 8, 8) };
        _allViews = [_ssa, new ConflictListViewModel(this)];
        SetViewOpen(ScopeToolbarButtons.ViewSsaName, true);
    }

    private ScopeDockViewModel ViewNamed(string name) =>
        _allViews.FirstOrDefault(v => v.Name == name) ?? throw new ArgumentException($"Unknown scope view '{name}'.", nameof(name));

    /// <summary>Starts <paramref name="view"/> reading and puts it among the open views, in dock order.</summary>
    private void Open(ScopeDockViewModel view)
    {
        view.StartReading();
        _openViews.Insert(_allViews.Take(Array.IndexOf(_allViews, view)).Count(_openViews.Contains), view);
    }

    /// <summary>Rebuilds the docked and floating projections of the open views, after an open, a close or a place change.</summary>
    private void SyncViewCollections()
    {
        Project(DockViews, v => v.Place is null);
        Project(FloatingViews, v => v.Place is not null);
        OnPropertyChanged(nameof(OpenViews));
        OnPropertyChanged(nameof(HasDockedViews));
    }

    /// <summary>Makes <paramref name="target"/> the open views that <paramref name="keep"/> keeps; an unchanged list is left alone.</summary>
    private void Project(ObservableCollection<ScopeDockViewModel> target, Func<ScopeDockViewModel, bool> keep)
    {
        ScopeDockViewModel[] wanted = [.. _openViews.Where(keep)];
        if (target.SequenceEqual(wanted))
        {
            return;
        }

        target.Clear();
        foreach (ScopeDockViewModel view in wanted)
        {
            target.Add(view);
        }
    }

    /// <summary>Stops every view's clock and subscriptions, as the Scope closes.</summary>
    private void StopViews()
    {
        foreach (ScopeDockViewModel view in _allViews)
        {
            view.StopReading();
        }
    }
}

/// <summary>
/// One view of the Scope, shown as a card: its name (as <c>.view</c> takes it), its title, its place, and its close and
/// dock actions. A view reads its data only while open.
/// </summary>
public abstract partial class ScopeDockViewModel(ScopeViewModel scope, string name, string title) : ObservableObject
{
    /// <summary>Where the view floats on the scope, or null while it sits in the dock; it survives the view closing.</summary>
    [ObservableProperty]
    private ScopePlacement? _place;

    /// <summary>The Scope the view belongs to.</summary>
    public ScopeViewModel Scope { get; } = scope;

    /// <summary>The view's name, as <c>.view</c> takes it.</summary>
    public string Name { get; } = name;

    /// <summary>The title the card's header shows.</summary>
    public string Title { get; } = title;

    /// <summary>Starts reading the view's data, as the view opens.</summary>
    internal abstract void StartReading();

    /// <summary>Stops reading the view's data, as the view closes or the Scope closes.</summary>
    internal abstract void StopReading();

    /// <summary>Closes the view through <c>.view {name} off</c>, as the card's close button does.</summary>
    [RelayCommand]
    private void Close() => Scope.Main.RunViewCommand(ScopeToolbarButtons.ViewCommand(Name, false));

    /// <summary>Returns the view to the dock through <c>.view {name} dock</c>, as a floating card's dock button does.</summary>
    [RelayCommand]
    private void Dock() => Scope.Main.RunViewCommand(ScopeToolbarButtons.ViewDockCommand(Name));
}

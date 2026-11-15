using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's views: the SSA and the LA/CA/MCI list. The open ones sit in the views dock in a fixed order (SSA, then
/// LA/CA/MCI); the SSA is open when the Scope opens. Every writer of a view's state (<c>.view</c>, the toolbar's VIEWS
/// toggles, a card's close button, a restored preference set) goes through <see cref="SetViewOpen"/>.
/// </summary>
public sealed partial class ScopeViewModel
{
    private SsaViewModel _ssa;

    /// <summary>Every view, in dock order.</summary>
    private ScopeDockViewModel[] _allViews;

    /// <summary>The open views, in dock order; the dock shows one card per item.</summary>
    public ObservableCollection<ScopeDockViewModel> DockViews { get; } = [];

    /// <summary>Whether any view is open; the dock is hidden otherwise.</summary>
    public bool HasOpenViews => DockViews.Count > 0;

    /// <summary>The open views' names, in dock order.</summary>
    public IReadOnlyList<string> OpenViews => [.. DockViews.Select(v => v.Name)];

    /// <summary>Whether the SSA's 1 s clock is running. Test seam.</summary>
    internal bool IsClockRunning => _ssa.IsClockRunning;

    /// <summary>Whether <paramref name="name"/> names a view (<c>ssa</c> or <c>ca</c>; lowercase).</summary>
    public static bool IsViewName(string name) => name is ScopeToolbarButtons.ViewSsaName or ScopeToolbarButtons.ViewCaName;

    /// <summary>Whether the view named <paramref name="name"/> is open.</summary>
    public bool IsViewOpen(string name) => DockViews.Any(v => v.Name == name);

    /// <summary>Opens or closes the view named <paramref name="name"/>; setting the state it has changes nothing.</summary>
    /// <exception cref="ArgumentException">The name is no view (<see cref="IsViewName"/>).</exception>
    public void SetViewOpen(string name, bool open)
    {
        ScopeDockViewModel view =
            _allViews.FirstOrDefault(v => v.Name == name) ?? throw new ArgumentException($"Unknown scope view '{name}'.", nameof(name));
        if (DockViews.Contains(view) == open)
        {
            return;
        }

        if (open)
        {
            view.StartReading();
            DockViews.Insert(_allViews.Take(Array.IndexOf(_allViews, view)).Count(DockViews.Contains), view);
        }
        else
        {
            view.StopReading();
            DockViews.Remove(view);
        }

        OnPropertyChanged(nameof(OpenViews));
        OnPropertyChanged(nameof(HasOpenViews));
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

    [MemberNotNull(nameof(_ssa), nameof(_allViews))]
    private void InitViews()
    {
        _ssa = new SsaViewModel(this);
        _allViews = [_ssa, new ConflictListViewModel(this)];
        SetViewOpen(ScopeToolbarButtons.ViewSsaName, true);
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
/// One view of the Scope, shown as a card: its name (as <c>.view</c> takes it), its title, and its close action. A view
/// reads its data only while open.
/// </summary>
public abstract partial class ScopeDockViewModel(ScopeViewModel scope, string name, string title) : ObservableObject
{
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
}

using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// A window hosting the YAAT Scope. A window that owns its <see cref="ScopeViewModel"/> releases it on close; the main
/// window's popped-out primary Scope does not, since that view model outlives the window and docks back into the tab.
/// </summary>
public partial class ScopeWindow : Window, IAlwaysOnTopToggle
{
    /// <summary>Window-geometry key.</summary>
    public const string GeometryKey = MainViewModel.ScopeViewKey;

    private readonly WindowGeometryHelper _geometryHelper;

    public ScopeWindow()
        : this(new UserPreferences(), null) { }

    public ScopeWindow(UserPreferences preferences, ScopeViewModel? viewModel)
        : this(preferences, viewModel, ownsViewModel: true) { }

    /// <param name="preferences">The preferences the window's geometry is kept in.</param>
    /// <param name="viewModel">The Scope the window shows.</param>
    /// <param name="ownsViewModel">Whether closing the window disposes <paramref name="viewModel"/>.</param>
    public ScopeWindow(UserPreferences preferences, ScopeViewModel? viewModel, bool ownsViewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _geometryHelper = new WindowGeometryHelper(this, preferences, GeometryKey, 1460, 800);
        _geometryHelper.Restore();
        Closed += (_, _) => Release(viewModel, ownsViewModel);
    }

    // A closed window drops its binding to the Scope first, so a view model that outlives it (the primary) keeps no
    // subscriber from it; only a window that owns the view model then disposes it.
    private void Release(ScopeViewModel? viewModel, bool ownsViewModel)
    {
        DataContext = null;
        if (ownsViewModel)
        {
            viewModel?.Dispose();
        }
    }

    /// <summary>The Scope control the window shows.</summary>
    internal ScopeHost ScopeHost => Host;

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}

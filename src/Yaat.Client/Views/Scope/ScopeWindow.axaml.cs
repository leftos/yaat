using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// A window hosting the YAAT Scope. A window that owns its <see cref="ScopeViewModel"/> releases it on close; the main
/// window's popped-out primary Scope does not, since that view model outlives the window and docks back into the tab.
/// </summary>
public partial class ScopeWindow : Window, IAlwaysOnTopToggle
{
    /// <summary>Window-geometry key of the primary Scope's window.</summary>
    public const string GeometryKey = MainViewModel.ScopeViewKey;

    private readonly WindowGeometryHelper _geometryHelper;

    public ScopeWindow()
        : this(new UserPreferences(), (ScopeViewModel?)null) { }

    public ScopeWindow(UserPreferences preferences, ScopeViewModel? viewModel)
        : this(preferences, viewModel, ownsViewModel: true) { }

    /// <param name="preferences">The preferences the window's geometry is kept in.</param>
    /// <param name="viewModel">The Scope the window shows.</param>
    /// <param name="ownsViewModel">Whether closing the window disposes <paramref name="viewModel"/>.</param>
    public ScopeWindow(UserPreferences preferences, ScopeViewModel? viewModel, bool ownsViewModel)
        : this(preferences, viewModel, ownsViewModel, GeometryKey) { }

    /// <summary>
    /// An extra Scope window, #2 and up: titled and keyed by its number. The main view model owns its view model and
    /// disposes it when the window closes for good.
    /// </summary>
    /// <param name="preferences">The preferences the window's geometry is kept in.</param>
    /// <param name="instance">The extra Scope the window shows.</param>
    public ScopeWindow(UserPreferences preferences, ScopeViewInstance instance)
        : this(preferences, instance.Vm, ownsViewModel: false, instance.GeometryKey)
    {
        Title = instance.Title;
    }

    private ScopeWindow(UserPreferences preferences, ScopeViewModel? viewModel, bool ownsViewModel, string geometryKey)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowGeometryKey = geometryKey;
        _geometryHelper = new WindowGeometryHelper(this, preferences, geometryKey, 1460, 800);
        _geometryHelper.Restore();
        Closed += (_, _) => Release(viewModel, ownsViewModel);
    }

    /// <summary>The key the window's geometry is saved under: <see cref="GeometryKey"/>, or <c>ScopeView#n</c> for extra window #n.</summary>
    public string WindowGeometryKey { get; }

    // A closed window drops its binding to the Scope first, so a view model that outlives it (the primary) keeps no
    // subscriber from it; only a window that owns the view model then disposes it. The canvas is the window's own, so it is
    // disposed too: the canvas's render lock makes that safe against a frame already drawing, and the post below the render
    // priority only orders the dispose after the UI-thread work the close queued.
    private void Release(ScopeViewModel? viewModel, bool ownsViewModel)
    {
        DataContext = null;
        ScopeCanvas canvas = ScopeHost.ScopeView.Canvas;
        Dispatcher.UIThread.Post(canvas.Dispose, DispatcherPriority.Background);
        if (ownsViewModel)
        {
            viewModel?.Dispose();
        }
    }

    /// <summary>The Scope control the window shows.</summary>
    internal ScopeHost ScopeHost => Host;

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}

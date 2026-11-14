using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>The pop-out window hosting the YAAT Scope. It owns its <see cref="ScopeViewModel"/> and releases it on close.</summary>
public partial class ScopeWindow : Window, IAlwaysOnTopToggle
{
    /// <summary>Window-geometry key.</summary>
    public const string GeometryKey = "ScopeView";

    private readonly WindowGeometryHelper _geometryHelper;

    public ScopeWindow()
        : this(new UserPreferences(), null) { }

    public ScopeWindow(UserPreferences preferences, ScopeViewModel? viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _geometryHelper = new WindowGeometryHelper(this, preferences, GeometryKey, 1000, 800);
        _geometryHelper.Restore();
        Closed += (_, _) => viewModel?.Dispose();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}

using System.ComponentModel;
using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Compare picker: Mine (the working copies) and Theirs (the server scenarios, the working copies and File…) side by
/// side, with the asked-over row preselected. Compare returns the pair, enabled while both sides name different
/// scenarios; Cancel, Esc or closing returns null.
/// </summary>
public partial class ScenarioComparePickerWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "ScenarioComparePicker";

    private readonly ScenarioComparePickerViewModel _picker;
    private readonly WindowGeometryHelper _geometry;

    // Parameterless ctor for the Avalonia designer / XamlLoader: it offers nothing.
    public ScenarioComparePickerWindow()
        : this(new ScenarioComparePickerViewModel([], [], null), new UserPreferences()) { }

    /// <summary>Creates the picker window over the picker's choices.</summary>
    /// <param name="picker">The scenarios each side offers and the sides picked.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    public ScenarioComparePickerWindow(ScenarioComparePickerViewModel picker, UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(preferences);
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 640, 420);
        _geometry.Restore();
        _picker = picker;
        MineList.ItemsSource = picker.MineOptions;
        MineList.SelectedItem = picker.SelectedMine;
        TheirsList.ItemsSource = picker.TheirsOptions;
        TheirsList.SelectedItem = picker.SelectedTheirs;
        MineList.SelectionChanged += (_, _) => _picker.SelectedMine = MineList.SelectedItem as ScenarioCompareSide;
        TheirsList.SelectionChanged += (_, _) => _picker.SelectedTheirs = TheirsList.SelectedItem as ScenarioCompareSide;
        CompareButton.Click += (_, _) => DialogPresenter.Close(this, _picker.Pick);
        CancelButton.Click += (_, _) => DialogPresenter.Close(this, null);
        picker.PropertyChanged += OnPickerChanged;
        Closed += (_, _) => _picker.PropertyChanged -= OnPickerChanged;
        ShowState();
    }

    /// <summary>Shows the picker over <paramref name="owner"/> and finishes with the picked pair.</summary>
    /// <param name="owner">The window the picker is modal over.</param>
    /// <returns>The pair, or null when the picker was cancelled or closed.</returns>
    public Task<ScenarioComparePick?> ShowAsync(Window owner) => DialogPresenter.ShowModalAsync<ScenarioComparePick?>(this, owner);

    private void OnPickerChanged(object? sender, PropertyChangedEventArgs e) => ShowState();

    private void ShowState()
    {
        CompareButton.IsEnabled = _picker.CanCompare;
        SameText.IsVisible = _picker.IsSameScenario;
    }
}

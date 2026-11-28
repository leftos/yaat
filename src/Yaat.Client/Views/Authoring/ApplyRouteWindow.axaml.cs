using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Apply Route, modal over the Scenario Editor, for its selected aircraft: a library route (the scenario's ARTCC, or every
/// ARTCC with All ARTCCs) or a route Find Route found, a stamp profile, Preview listing each field the stamp would change
/// under the Aircraft tab's label with its warnings, and Apply, which writes the stamp as one undo step and closes the
/// window. A warning starting "Error:" disables Apply. Cancel and the close box write nothing. The Profiles expander edits
/// the profile the list selects, or a new one, and saves, duplicates and deletes it; Delete waits for a profile that was
/// saved. Closing disposes the view model and any Find Route still open.
/// </summary>
public partial class ApplyRouteWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "ApplyRoute";

    private readonly Wiring? _wiring;
    private readonly WindowGeometryHelper? _geometry;
    private RouteFinderViewModel? _finder;

    // Set while the list boxes and the editor's lists are copied one into the other.
    private bool _syncingLists;

    // The problems of each list box whose text does not read; Save and Duplicate are refused while any remain.
    private readonly Dictionary<TextBox, IReadOnlyList<string>> _listProblems = [];

    // The problem the editor's Message shows, so a fixed list clears only its own message.
    private string? _shownProblem;

    // Parameterless ctor for the Avalonia designer / XamlLoader: the layout alone, over no aircraft.
    public ApplyRouteWindow()
    {
        InitializeComponent();
    }

    private ApplyRouteWindow(Wiring wiring, UserPreferences preferences)
    {
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _wiring = wiring;
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 900, 760);
        _geometry.Restore();
        string callsign = wiring.Aircraft.Selected?.Callsign ?? "";
        Title = $"Apply Route — {callsign}";
        HeaderText.Text = $"Apply Route to {callsign}";
        DataContext = wiring.ViewModel;
        Wire(wiring);
        ShowLists();
        RefreshApply();
        RefreshDelete();
    }

    /// <summary>
    /// Shows a number field's value as text and reads it back: blank is null, finite invariant digits the number, and
    /// anything else (a fraction for a whole number, NaN, infinity, letters) a validation error.
    /// </summary>
    public static IValueConverter Number { get; } = new NumberConverter();

    /// <summary>Creates the window over Apply Route for the Aircraft tab's selected aircraft.</summary>
    /// <param name="viewModel">Apply Route, which the window disposes when it closes.</param>
    /// <param name="aircraft">The Aircraft tab Apply Route writes through; its edit closes the window.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="findRoute">
    /// Opens Find Route for the aircraft, its Apply handing the route to Apply Route, or null when it cannot; null for no
    /// Find Route, which disables its button.
    /// </param>
    /// <returns>The window, not yet shown.</returns>
    public static ApplyRouteWindow Create(
        ApplyRouteViewModel viewModel,
        ScenarioAircraftViewModel aircraft,
        UserPreferences preferences,
        Func<RouteFinderViewModel?>? findRoute
    )
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(preferences);
        return new ApplyRouteWindow(new Wiring(viewModel, aircraft, findRoute), preferences);
    }

    /// <summary>Whether the warnings hold one starting "Error:", which disables Apply.</summary>
    /// <param name="warnings">The preview's warnings.</param>
    /// <returns>True when one does.</returns>
    public static bool BlocksApply(IEnumerable<string> warnings) =>
        warnings.Any(w => w.StartsWith(ApplyRouteViewModel.ErrorPrefix, StringComparison.Ordinal));

    /// <summary>Shows the window modal over <paramref name="owner"/> and finishes when it closes.</summary>
    /// <param name="owner">The Scenario Editor.</param>
    /// <returns>The close.</returns>
    public Task ShowAsync(Window owner) => DialogPresenter.ShowModalAsync(this, owner);

    private void Wire(Wiring wiring)
    {
        ApplyRouteViewModel vm = wiring.ViewModel;
        ApplyButton.Click += OnApplyClick;
        CancelButton.Click += OnCancelClick;
        NewProfileButton.Click += OnNewProfileClick;
        DeleteProfileButton.Click += OnDeleteProfileClick;
        SaveProfileButton.Click += OnSaveProfileClick;
        DuplicateProfileButton.Click += OnDuplicateProfileClick;
        FindRouteButton.IsEnabled = wiring.FindRoute is not null;
        vm.ApplyCommand.CanExecuteChanged += OnApplyStateChanged;
        vm.Warnings.CollectionChanged += OnApplyStateChanged;
        vm.Profiles.CollectionChanged += OnProfilesChanged;
        vm.Editor.PropertyChanged += OnEditorPropertyChanged;
        foreach ((TextBox box, INotifyCollectionChanged list) in ListBoxes(vm.Editor))
        {
            box.TextChanged += OnListTextChanged;
            list.CollectionChanged += OnEditorListChanged;
        }

        foreach (NumberBox number in NumberBoxes())
        {
            number.Box.PropertyChanged += OnNumberErrorsChanged;
        }

        if (FindRouteButton.Flyout is Flyout flyout)
        {
            flyout.Opening += OnFindRouteOpening;
            flyout.Closed += OnFindRouteClosed;
            FinderView.CloseRequested += OnFinderDone;
        }

        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_wiring is not { } wiring)
        {
            return;
        }

        ApplyRouteViewModel vm = wiring.ViewModel;
        vm.ApplyCommand.CanExecuteChanged -= OnApplyStateChanged;
        vm.Warnings.CollectionChanged -= OnApplyStateChanged;
        vm.Profiles.CollectionChanged -= OnProfilesChanged;
        vm.Editor.PropertyChanged -= OnEditorPropertyChanged;
        foreach ((TextBox box, INotifyCollectionChanged list) in ListBoxes(vm.Editor))
        {
            box.TextChanged -= OnListTextChanged;
            list.CollectionChanged -= OnEditorListChanged;
        }

        foreach (NumberBox number in NumberBoxes())
        {
            number.Box.PropertyChanged -= OnNumberErrorsChanged;
        }

        FinderView.CloseRequested -= OnFinderDone;
        CloseFinder();
        vm.Dispose();
    }

    // Apply writes only what Preview showed with no "Error:" line, and the window closes once the Aircraft tab took the
    // edit; an Apply that wrote nothing (a skip or a route that stopped converting) leaves it open with the reason shown.
    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if ((_wiring is not { } wiring) || !CanApply(wiring.ViewModel))
        {
            return;
        }

        bool wrote = false;
        void OnEdited(object? s, EventArgs a) => wrote = true;
        wiring.Aircraft.Edited += OnEdited;
        try
        {
            wiring.ViewModel.ApplyCommand.Execute(null);
        }
        finally
        {
            wiring.Aircraft.Edited -= OnEdited;
        }

        if (wrote)
        {
            Close();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnApplyStateChanged(object? sender, EventArgs e) => RefreshApply();

    private void RefreshApply()
    {
        if (_wiring is not { } wiring)
        {
            return;
        }

        ApplyButton.IsEnabled = CanApply(wiring.ViewModel);
        ErrorBlockText.IsVisible = BlocksApply(wiring.ViewModel.Warnings);
    }

    private static bool CanApply(ApplyRouteViewModel vm) => vm.ApplyCommand.CanExecute(null) && !BlocksApply(vm.Warnings);

    // New holds a fresh profile in the editor: dropping the list's selection does it, or the editor itself with none.
    private void OnNewProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_wiring is not { ViewModel: { } vm })
        {
            return;
        }

        if (vm.SelectedProfile is null)
        {
            vm.Editor.New();
        }
        else
        {
            vm.SelectedProfile = null;
        }

        RefreshDelete();
    }

    private void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        if ((_wiring is { ViewModel: { } vm }) && IsSaved(vm))
        {
            vm.Editor.DeleteCommand.Execute(null);
        }
    }

    // Save and Duplicate write the editor's profile only while every list box and number box reads; else the first
    // problem shows.
    private void OnSaveProfileClick(object? sender, RoutedEventArgs e) => WriteProfile(e, save: true);

    private void OnDuplicateProfileClick(object? sender, RoutedEventArgs e) => WriteProfile(e, save: false);

    private void WriteProfile(RoutedEventArgs e, bool save)
    {
        e.Handled = true;
        if (_wiring is not { ViewModel.Editor: { } editor })
        {
            return;
        }

        if (FirstProblem() is { } problem)
        {
            ShowProblem(editor, problem);
            return;
        }

        (save ? editor.SaveCommand : editor.DuplicateCommand).Execute(null);
    }

    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDelete();

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(StampProfileEditorViewModel.Id), StringComparison.Ordinal))
        {
            RefreshDelete();
        }
    }

    // Delete is offered only for a profile the store lists: one never saved has no file to delete.
    private void RefreshDelete()
    {
        if (_wiring is { ViewModel: { } vm })
        {
            DeleteProfileButton.IsEnabled = IsSaved(vm);
        }
    }

    private static bool IsSaved(ApplyRouteViewModel vm) =>
        vm.Profiles.Any(p => string.Equals(p.Id, vm.Editor.Id, StringComparison.OrdinalIgnoreCase));

    // Find Route opens a search for the aircraft; its Apply hands the route to Apply Route and closes the flyout.
    private void OnFindRouteOpening(object? sender, EventArgs e)
    {
        CloseFinder();
        if (_wiring is not { } wiring)
        {
            return;
        }

        _finder = wiring.FindRoute?.Invoke();
        FinderView.DataContext = _finder;
        if (_finder is not { } finder)
        {
            // No Find Route can be built for this aircraft: the button stays off.
            FindRouteButton.IsEnabled = false;
            Dispatcher.UIThread.Post(() => FindRouteButton.Flyout?.Hide());
            return;
        }

        finder.Applied += OnFinderDone;
        finder.CloseRequested += OnFinderDone;
    }

    private void OnFinderDone(object? sender, EventArgs e) => FindRouteButton.Flyout?.Hide();

    private void OnFindRouteClosed(object? sender, EventArgs e) => CloseFinder();

    private void CloseFinder()
    {
        if (_finder is not { } finder)
        {
            return;
        }

        finder.Applied -= OnFinderDone;
        finder.CloseRequested -= OnFinderDone;
        finder.Dispose();
        _finder = null;
        FinderView.DataContext = null;
    }

    // The list boxes of the profile editor, each with the editor list it shows.
    private IEnumerable<(TextBox Box, INotifyCollectionChanged List)> ListBoxes(StampProfileEditorViewModel editor) =>
        [
            (StarAliasesBox, editor.StarAliases),
            (PresetCommandsBox, editor.PresetCommands),
            (AircraftTypesBox, editor.AircraftTypes),
            (CallsignPrefixesBox, editor.CallsignPrefixes),
            (RunwaysBox, editor.Runways),
            (ParkingSpotsBox, editor.ParkingSpots),
        ];

    private void OnEditorListChanged(object? sender, NotifyCollectionChangedEventArgs e) => ShowLists();

    // Each list box shows its editor list, as Load filled it or as the author typed it.
    private void ShowLists()
    {
        if (_syncingLists || (_wiring is not { ViewModel.Editor: { } editor }))
        {
            return;
        }

        _syncingLists = true;
        _listProblems.Clear();
        try
        {
            StarAliasesBox.Text = ProfileListText.JoinAliases(editor.StarAliases);
            PresetCommandsBox.Text = ProfileListText.JoinPresets(editor.PresetCommands);
            AircraftTypesBox.Text = ProfileListText.JoinPool(editor.AircraftTypes);
            CallsignPrefixesBox.Text = ProfileListText.JoinPool(editor.CallsignPrefixes);
            RunwaysBox.Text = ProfileListText.JoinPool(editor.Runways);
            ParkingSpotsBox.Text = ProfileListText.JoinPool(editor.ParkingSpots);
        }
        finally
        {
            _syncingLists = false;
        }
    }

    // A list box the author typed in replaces its editor list with what the text reads as.
    private void OnListTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingLists || (sender is not TextBox box) || (_wiring is not { ViewModel.Editor: { } editor }))
        {
            return;
        }

        _syncingLists = true;
        try
        {
            ReadList(box, editor);
        }
        finally
        {
            _syncingLists = false;
        }

        RefreshProblem(editor);
    }

    // The first problem any list box or number box stands on, the list boxes' first.
    private string? FirstProblem() => FirstListProblem() ?? FirstNumberProblem();

    private string? FirstListProblem() => _listProblems.Values.SelectMany(p => p).FirstOrDefault();

    // The first number box whose binding holds a validation error, in the window's order: its text is not the number the
    // field reads, so Save and Duplicate would write the value the box showed before the bad text.
    private string? FirstNumberProblem()
    {
        foreach (NumberBox number in NumberBoxes())
        {
            if (DataValidationErrors.GetHasErrors(number.Box))
            {
                return string.Create(CultureInfo.InvariantCulture, $"{number.Label}: \"{number.Box.Text}\" is not a number");
            }
        }

        return null;
    }

    // Shows the first problem, and clears the message it showed once nothing is wrong any more.
    private void RefreshProblem(StampProfileEditorViewModel editor)
    {
        if (FirstProblem() is { } problem)
        {
            ShowProblem(editor, problem);
        }
        else if ((_shownProblem is not null) && string.Equals(editor.Message, _shownProblem, StringComparison.Ordinal))
        {
            editor.Message = null;
            _shownProblem = null;
        }
    }

    // The profile editor's number boxes, each with the label its field shows, in the window's order.
    private IEnumerable<NumberBox> NumberBoxes() =>
        [
            new(StartingAltitudeBox, "Altitude (ft)"),
            new(StartingSpeedBox, "Speed (kt)"),
            new(StartingHeadingBox, "Heading (deg)"),
            new(CruiseAltitudeBox, "Cruise altitude (ft)"),
            new(CruiseSpeedBox, "Cruise speed (kt)"),
            new(HandoffDelayBox, "Handoff delay (seconds)"),
        ];

    // The binding's own validation state is the only record of a box whose text does not read, so the message line
    // follows it as the error comes and goes.
    private void OnNumberErrorsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if ((e.Property == DataValidationErrors.ErrorsProperty) && (_wiring is { ViewModel.Editor: { } editor }))
        {
            RefreshProblem(editor);
        }
    }

    private void ShowProblem(StampProfileEditorViewModel editor, string problem)
    {
        _shownProblem = problem;
        editor.Message = problem;
    }

    private void ReadList(TextBox box, StampProfileEditorViewModel editor)
    {
        if (ReferenceEquals(box, StarAliasesBox))
        {
            ProfileListRead<StarAlias> read = ProfileListText.ReadAliases(box.Text);
            _listProblems[box] = read.Problems;
            Replace(editor.StarAliases, read.Items);
        }
        else if (ReferenceEquals(box, PresetCommandsBox))
        {
            ProfileListRead<StampPresetCommand> read = ProfileListText.ReadPresets(box.Text);
            _listProblems[box] = read.Problems;
            Replace(editor.PresetCommands, read.Items);
        }
        else
        {
            ObservableCollection<string>? pool =
                ReferenceEquals(box, AircraftTypesBox) ? editor.AircraftTypes
                : ReferenceEquals(box, CallsignPrefixesBox) ? editor.CallsignPrefixes
                : ReferenceEquals(box, RunwaysBox) ? editor.Runways
                : ReferenceEquals(box, ParkingSpotsBox) ? editor.ParkingSpots
                : null;
            if (pool is not null)
            {
                Replace(pool, ProfileListText.SplitPool(box.Text));
            }
        }
    }

    private static void Replace<T>(IList<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }

    // What the window works over: Apply Route, the Aircraft tab it writes through, and how it opens Find Route.
    private sealed record Wiring(ApplyRouteViewModel ViewModel, ScenarioAircraftViewModel Aircraft, Func<RouteFinderViewModel?>? FindRoute);

    // A number box of the profile editor: the box, and the label its field shows.
    private sealed record NumberBox(TextBox Box, string Label);

    // A nullable number as text: null is blank, a value is its invariant digits; blank text reads back as null, digits
    // as the number when it is finite, and anything else as a validation error that leaves the value as it was.
    private sealed class NumberConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            (value as IFormattable)?.ToString(null, CultureInfo.InvariantCulture) ?? "";

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string text = ((value as string) ?? "").Trim();
            if (text.Length == 0)
            {
                return null;
            }

            Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if ((type == typeof(int)) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole))
            {
                return whole;
            }

            if (
                (type == typeof(double))
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number)
            )
            {
                return number;
            }

            return new BindingNotification(new FormatException($"\"{text}\" is not a number"), BindingErrorType.DataValidationError);
        }
    }
}

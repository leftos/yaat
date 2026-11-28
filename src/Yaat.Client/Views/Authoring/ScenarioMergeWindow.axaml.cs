using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia.Enums;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The scenario merge window, modal over the Scenario Editor, over a <see cref="ScenarioMergeViewModel"/>: the toolbar
/// (summary, Previous/Next, Show base, Only differences, the Whole scenario menu), the two-way note, the General and Aircraft
/// tabs with their open-clash counts, the MINE/THEIRS caption row, the General tab's merge lines with their gutters, BASE and
/// MERGED VALUE strips, and the footer. Apply runs the view model's Apply with the given checks, any findings shown in one
/// "Merged with problems" box; the window closes with the result once the merge is written, and with null on Cancel, the close
/// box, or after Fresh copy beside makes its copy.
/// </summary>
public partial class ScenarioMergeWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "ScenarioMerge";

    private const string WarningsTitle = "Merged with problems";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioMergeWindow>();

    private readonly ScenarioMergeChecks _checks;
    private readonly WindowGeometryHelper _geometry;
    private Border? _currentLine;
    private bool _closed;

    // Set when the window closed before the view model's outcome: an Apply still running then writes after it closed.
    private bool _closedUnfinished;

    // Set while a Fresh copy beside runs; a second click does nothing.
    private bool _freshCopyRunning;

    // Parameterless ctor for the Avalonia designer / XamlLoader: an empty merge that checks nothing.
    public ScenarioMergeWindow()
        : this(DesignRequest(), new UserPreferences(), _ => []) { }

    /// <summary>Creates the window over a merge of the request's texts.</summary>
    /// <param name="request">The texts, captions and writer.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="runChecks">Runs the Scenario Editor's checks over the merged text at Apply; its findings, empty for none.</param>
    /// <exception cref="System.Text.Json.JsonException">A text is malformed or not a JSON object.</exception>
    public ScenarioMergeWindow(ScenarioMergeRequest request, UserPreferences preferences, Func<string, IReadOnlyList<ScenarioEditIssue>> runChecks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(runChecks);
        ViewModel = ScenarioMergeViewModel.Create(request);
        _checks = new ScenarioMergeChecks(runChecks, ShowWarningsAsync);
        DataContext = ViewModel;
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 1100, 720);
        _geometry.Restore();
        FreshCopyItem.IsVisible = request.FreshCopy is not null;
        FreshCopyItem.Click += OnFreshCopyClick;
        ViewModel.CancelCommand.CanExecuteChanged += OnCancelCanExecuteChanged;
        ApplyButton.Click += OnApplyClick;
        Tabs.SelectionChanged += OnTabChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;
        AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        Closing += OnClosing;
        Closed += OnClosed;
        _ = CloseOnCompletionAsync();
    }

    /// <summary>Gets the window's view model.</summary>
    public ScenarioMergeViewModel ViewModel { get; }

    /// <summary>Shows the window over <paramref name="owner"/> and finishes with what Apply wrote.</summary>
    /// <param name="owner">The Scenario Editor the window is modal over.</param>
    /// <returns>The applied merge; null when the author cancelled, closed the window or made a fresh copy.</returns>
    public Task<ScenarioMergeResult?> ShowAsync(Window owner) => DialogPresenter.ShowModalAsync<ScenarioMergeResult>(this, owner);

    private static ScenarioMergeRequest DesignRequest() =>
        new("ZOA", "design", new ScenarioMergeTexts("{}", "{}", "{}"), new("Merge", "mine", "theirs", "base", ""), _ => Task.CompletedTask);

    // The findings box: the summary line, then one line per finding, its callsign first when it has one. No blank line: a
    // message with one never finishes laying out in MessageBox.Avalonia under the headless platform.
    private static string WarningsText(IReadOnlyList<ScenarioEditIssue> warnings)
    {
        string noun = (warnings.Count == 1) ? "problem" : "problems";
        List<string> lines = [$"The merged scenario has {warnings.Count} {noun}; it will be applied and the editor will mark them."];
        lines.AddRange(warnings.Select(w => (w.Callsign is { } callsign) ? $"• {callsign} · {w.Message}" : $"• {w.Message}"));
        return string.Join("\n", lines);
    }

    private async Task ShowWarningsAsync(IReadOnlyList<ScenarioEditIssue> warnings) =>
        await MessageBoxPresenter.ShowStandardAsync(this, WarningsTitle, WarningsText(warnings), ButtonEnum.Ok).ConfigureAwait(true);

    // The view model's outcome closes the window: the result once Apply wrote, null once it was cancelled.
    private async Task CloseOnCompletionAsync()
    {
        ScenarioMergeResult? result = await ViewModel.Completion.ConfigureAwait(true);
        if (!_closed)
        {
            DialogPresenter.Close(this, result);
        }
    }

    // Apply; a failure the view model does not catch shows on the footer. A merge written after the window closed is logged.
    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        ApplyFailureText.IsVisible = false;
        try
        {
            ScenarioMergeResult? result = await ViewModel.ApplyAsync(_checks).ConfigureAwait(true);
            if ((result is not null) && _closedUnfinished)
            {
                Log.LogWarning("Merge window: the merge was written after the window closed");
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Merge window: Apply failed");
            ApplyFailureText.Text = "Couldn't apply: " + ex.Message;
            ApplyFailureText.IsVisible = true;
        }
    }

    // Fresh copy beside is offered only while no Apply runs (Cancel is offered exactly then) and no fresh copy is being made.
    private bool CanFreshCopy() => !_freshCopyRunning && ViewModel.CancelCommand.CanExecute(null);

    private void UpdateFreshCopyItem() => FreshCopyItem.IsEnabled = CanFreshCopy();

    private void OnCancelCanExecuteChanged(object? sender, EventArgs e) => UpdateFreshCopyItem();

    // Fresh copy beside: the window closes with nothing applied once the copy is made, and stays open with the failure if not.
    private async void OnFreshCopyClick(object? sender, RoutedEventArgs e)
    {
        if ((ViewModel.Request.FreshCopy is not { } freshCopy) || !CanFreshCopy())
        {
            return;
        }

        FreshCopyErrorText.IsVisible = false;
        _freshCopyRunning = true;
        UpdateFreshCopyItem();
        try
        {
            await freshCopy().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Merge window: making a fresh copy beside the working copy failed");
            FreshCopyErrorText.Text = "Couldn't make a fresh copy: " + ex.Message;
            FreshCopyErrorText.IsVisible = true;
            return;
        }
        finally
        {
            _freshCopyRunning = false;
            UpdateFreshCopyItem();
        }

        DialogPresenter.Close(this, null);
    }

    // The close box cancels the merge; while an Apply runs it is refused, though code and the owner closing still close it.
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (ViewModel.Completion.IsCompleted)
        {
            return;
        }

        if (ViewModel.CancelCommand.CanExecute(null))
        {
            ViewModel.CancelCommand.Execute(null);
        }
        else if (DialogPresenter.ShouldCancelOwnerClose(e.CloseReason, e.IsProgrammatic))
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _closedUnfinished = !ViewModel.Completion.IsCompleted;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.CancelCommand.CanExecuteChanged -= OnCancelCanExecuteChanged;
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs) && (Tabs.SelectedIndex >= 0))
        {
            ViewModel.SelectedTab = (ScenarioMergeTab)Tabs.SelectedIndex;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScenarioMergeViewModel.SelectedTab))
        {
            Tabs.SelectedIndex = (int)ViewModel.SelectedTab;
        }

        // The current clash's line is found once its tab shows and has been laid out: Previous/Next sets the clash before
        // the tab, and a tab out of the tree holds no lines.
        if (e.PropertyName is nameof(ScenarioMergeViewModel.SelectedTab) or nameof(ScenarioMergeViewModel.CurrentClash))
        {
            Dispatcher.UIThread.Post(MarkCurrentAfterLayout, DispatcherPriority.Loaded);
        }
    }

    private void MarkCurrentAfterLayout()
    {
        if (_closed)
        {
            return;
        }

        UpdateLayout();
        MarkCurrent(ViewModel.CurrentClash);
    }

    // Marks the line of the clash Previous/Next selected and scrolls it into view.
    private void MarkCurrent(ScenarioMergeRowViewModel? row)
    {
        _currentLine?.Classes.Remove("current");
        _currentLine =
            (row is null)
                ? null
                : SectionList
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Classes.Contains("lineBox") && ReferenceEquals(b.DataContext, row));
        _currentLine?.Classes.Add("current");
        _currentLine?.BringIntoView();
    }

    private static void OnFieldLostFocus(object? sender, FocusChangedEventArgs e) => Commit(e.Source);

    private static void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit(e.Source);
        }
    }

    // Runs the commit command a MERGED VALUE box names in its Tag, as the editor's boxes do.
    private static void Commit(object? source)
    {
        if ((source is TextBox { Tag: ICommand command }) && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}

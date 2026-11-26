using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Views;

/// <summary>
/// The Rundowns window: the room's STARS RD lists, one section per list in the order the server sends them, then the
/// hold-for-release rundown and its release buttons. A right click on an RD row opens the entry's coordination menu, the
/// one the Radar view's RUNDOWN list opens. One window per main view model; layouts keep its open state.
/// </summary>
public partial class RundownsWindow : Window, IAlwaysOnTopToggle
{
    private static readonly ILogger Log = AppLog.CreateLogger("RundownsWindow");

    private static readonly ConditionalWeakTable<MainViewModel, RundownsWindow> OpenWindows = [];

    private readonly WindowGeometryHelper _geometryHelper;

    // Rebuilds the rows once a second while the window is open, so the countdowns run.
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private MainViewModel? _vm;
    private ContextMenu? _activeContextMenu;

    // The rows' monospace font, looked up on the first rebuild; null when the resource is missing.
    private FontFamily? _monoFont;
    private bool _monoFontLookedUp;

    /// <summary>Creates the window with fresh preferences (the designer's constructor).</summary>
    public RundownsWindow()
        : this(new UserPreferences()) { }

    /// <summary>Creates the window, its geometry restored from <paramref name="preferences"/>.</summary>
    public RundownsWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "Rundowns", 520, 440);
        _geometryHelper.Restore();
        _clock.Tick += (_, _) => Refresh();
        Opened += (_, _) => _clock.Start();
        Closed += (_, _) =>
        {
            _clock.Stop();
            Detach();
        };
    }

    /// <summary>Sends an RD entry menu's command for a callsign in place of the server send, when set. Test seam.</summary>
    internal Func<string, string, Task>? CommandSender { get; set; }

    /// <summary>The entry menu a right click last opened, while it is open.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <summary>True while a Rundowns window is open for this view model (layouts capture this).</summary>
    public static bool IsOpen(MainViewModel vm) => OpenWindows.TryGetValue(vm, out _);

    /// <summary>Closes the Rundowns window for this view model, if one is open.</summary>
    public static void Close(MainViewModel vm)
    {
        if (OpenWindows.TryGetValue(vm, out RundownsWindow? existing))
        {
            existing.Close();
        }
    }

    /// <summary>Activates the view model's Rundowns window, or opens one bound to it.</summary>
    public static RundownsWindow ShowOrActivate(MainViewModel vm)
    {
        if (OpenWindows.TryGetValue(vm, out RundownsWindow? existing))
        {
            existing.RestoreAndActivate();
            return existing;
        }

        var window = new RundownsWindow(vm.Preferences) { DataContext = vm };
        OpenWindows.Add(vm, window);
        window.Closed += (_, _) =>
        {
            OpenWindows.Remove(vm);

            // Only a user-initiated close clears the persisted "open" flag; the app's shutdown closes the window too, and
            // the flag must survive it so the next launch reopens the window.
            if (!AppLifetime.IsShuttingDown)
            {
                vm.Preferences.SetRundownsWindowOpen(false);
            }
        };
        vm.Preferences.SetRundownsWindowOpen(true);

        // Shown un-owned, like every other persistent tool window, so it can be sent behind the main window.
        window.Show();
        return window;
    }

    /// <inheritdoc />
    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        if (DataContext is MainViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.Rundown.CollectionChanged += OnRundownChanged;
        }

        Refresh();
    }

    private void Detach()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Rundown.CollectionChanged -= OnRundownChanged;
        _vm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CoordinationLists) or nameof(MainViewModel.HoldForReleaseActive))
        {
            Refresh();
        }
    }

    private void OnRundownChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    // Rebuilds the RD sections and decides whether the window has nothing to show; the clock's tick calls it once a second.
    internal void Refresh()
    {
        ListsPanel.Children.Clear();
        if (_vm is not { } vm)
        {
            EmptyText.IsVisible = true;
            return;
        }

        int rows = 0;
        // A list with no items has no section, so the empty line never sits under bare headers.
        foreach (CoordinationListHubDto list in vm.CoordinationLists.Where(l => l.Items.Count > 0))
        {
            rows += AddSection(list, vm.ScenarioElapsedSeconds);
        }

        EmptyText.IsVisible = (rows == 0) && (!vm.HoldForReleaseActive);
    }

    // One list's section: its title (its list id when the title is empty), then its rows; returns the row count.
    private int AddSection(CoordinationListHubDto list, double scenarioElapsedSeconds)
    {
        string title = string.IsNullOrWhiteSpace(list.Title) ? list.ListId : list.Title;
        ListsPanel.Children.Add(
            new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 4, 0, 2),
            }
        );

        IReadOnlyList<RundownRow> rows = RundownRows.Build([list], scenarioElapsedSeconds);
        FontFamily? mono = MonoFont();
        foreach (RundownRow row in rows)
        {
            var block = new TextBlock
            {
                Text = row.Text,
                FontSize = 12,
                Foreground = (row.Color == RundownRowColor.Caution) ? Brushes.Yellow : Brushes.LimeGreen,
                Background = Brushes.Transparent,
            };
            if (mono is not null)
            {
                block.FontFamily = mono;
            }

            block.PointerPressed += (_, e) => OnRowPressed(row, block, e);
            ListsPanel.Children.Add(block);
        }

        return rows.Count;
    }

    private FontFamily? MonoFont()
    {
        if (!_monoFontLookedUp)
        {
            _monoFontLookedUp = true;
            _monoFont = (this.TryFindResource("MonoFont", out object? font) && (font is FontFamily family)) ? family : null;
            if (_monoFont is null)
            {
                Log.LogWarning("MonoFont resource not found; the Rundowns rows draw in the default font");
            }
        }

        return _monoFont;
    }

    private void OnRowPressed(RundownRow row, TextBlock block, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(block).Properties.IsRightButtonPressed)
        {
            return;
        }

        e.Handled = true;
        OpenEntryMenu(row, e.GetPosition(ListsPanel));
    }

    // The entry the row names, found in the room's lists; one that is gone by now opens nothing. The menu and its text prompt
    // anchor on the lists panel, which the once-a-second rebuild keeps, rather than on the row, which it replaces.
    private void OpenEntryMenu(RundownRow row, Point at)
    {
        if (_vm is not { } vm)
        {
            return;
        }

        CoordinationListHubDto? list = vm.CoordinationLists.FirstOrDefault(l => l.ListId == row.ListId);
        CoordinationItemHubDto? item = list?.Items.FirstOrDefault(i => i.Id == row.ItemId);
        if ((list is null) || (item is null))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Rundowns row {ListId}/{ItemId} is no longer in the room's lists", row.ListId, row.ItemId);
            }

            return;
        }

        ShowContextMenu(
            RundownEntryMenu.Build(
                RundownEntryMenu.Items(list, item),
                command => _ = SendRundownCommand(vm, item.Callsign, command),
                placeholder => RundownTextPrompt.AskAsync(ListsPanel, at, placeholder)
            )
        );
    }

    // An entry menu's command, sent for the entry's aircraft.
    private Task SendRundownCommand(MainViewModel vm, string callsign, string command)
    {
        if (CommandSender is { } sender)
        {
            return sender(callsign, command);
        }

        AircraftModel? aircraft = vm.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        var host = new ClientMenuHost(vm, aircraft, ListsPanel);
        return host.SendAsync(callsign, command, host.Session.Initials);
    }

    // One menu at a time: opening one closes the last.
    private void ShowContextMenu(ContextMenu menu)
    {
        _activeContextMenu?.Close();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (_activeContextMenu == menu)
            {
                _activeContextMenu = null;
            }
        };
        menu.PlacementTarget = ListsPanel;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(ListsPanel);
    }
}

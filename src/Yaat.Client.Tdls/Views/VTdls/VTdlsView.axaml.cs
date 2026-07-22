using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Find;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.VTdls;

/// <summary>
/// Code-behind for the vTDLS view. Handles user input the XAML can't express
/// cleanly: keyboard shortcuts (F4 Dump, F10 close-editor, F12 Send), facility
/// switcher MenuFlyout, and the Zulu-clock tick. The view never mutates state
/// directly — every action funnels through <see cref="VTdlsViewModel"/> which
/// emits canonical commands.
/// </summary>
public partial class VTdlsView : UserControl
{
    private static readonly ILogger Log = SimLog.CreateLogger("VTdlsView");

    private DispatcherTimer? _clockTimer;

    // Shared in-view Find (Ctrl+F). Snapshot = DCL then PDC items; scrollTo brings the
    // matched row into view in whichever list holds it. Its DataContext drives the FindBar.
    private readonly FindController _findController;

    /// <summary>Test hook: the in-view Find controller backing the FindBar overlay.</summary>
    internal FindController FindController => _findController;

    /// <summary>Test hook: the rendered footer status line, read off the live control rather than the view-model.</summary>
    internal string? FooterStatusText => FooterStatus.Text;

    /// <summary>Test hook: whether the footer status carries the warning class that repaints it.</summary>
    internal bool FooterStatusIsWarning => FooterStatus.Classes.Contains("warning");

    public VTdlsView()
    {
        InitializeComponent();

        _findController = new FindController(BuildFindSnapshot, ScrollFindMatchIntoView);
        FindBar.DataContext = _findController;

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    private VTdlsViewModel? _trackedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged -= OnVmPropertyChanged;
            _trackedVm.DclItems.CollectionChanged -= OnFindItemsChanged;
            _trackedVm.PdcItems.CollectionChanged -= OnFindItemsChanged;
        }
        _trackedVm = DataContext as VTdlsViewModel;
        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged += OnVmPropertyChanged;
            _trackedVm.DclItems.CollectionChanged += OnFindItemsChanged;
            _trackedVm.PdcItems.CollectionChanged += OnFindItemsChanged;
            ApplyTheme(_trackedVm.IsDarkMode);
        }
        _findController.Refresh();
    }

    // ── In-view Find (Ctrl+F) ───────────────────────────────────

    private void OnFindItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => _findController.Refresh();

    /// <summary>Searchable rows in display order: DCL list then PDC list (CPDLC is always empty).</summary>
    private IReadOnlyList<IFindableItem> BuildFindSnapshot()
    {
        var result = new List<IFindableItem>();
        if (DataContext is VTdlsViewModel vm)
        {
            result.AddRange(vm.DclItems);
            result.AddRange(vm.PdcItems);
        }
        return result;
    }

    private void ScrollFindMatchIntoView(IFindableItem item)
    {
        if (item is not TdlsItemViewModel target)
        {
            return;
        }
        Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is not VTdlsViewModel vm)
                {
                    return;
                }
                if (vm.DclItems.Contains(target))
                {
                    DclListBox.ScrollIntoView(target);
                }
                else if (vm.PdcItems.Contains(target))
                {
                    PdcListBox.ScrollIntoView(target);
                }
            },
            DispatcherPriority.Loaded
        );
    }

    /// <summary>Handles the Find keys (Ctrl+F / F3 / Shift+F3 / Esc); returns true if consumed.</summary>
    private bool HandleFindKeys(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && (e.Key == Key.F))
        {
            _findController.Open();
            FindBar.FocusInput();
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.F3)
        {
            if (shift)
            {
                _findController.Previous();
            }
            else
            {
                _findController.Next();
            }
            e.Handled = true;
            return true;
        }
        if ((e.Key == Key.Escape) && _findController.IsVisible)
        {
            _findController.Close();
            Focus();
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VTdlsViewModel.IsDarkMode) && sender is VTdlsViewModel vm)
        {
            Dispatcher.UIThread.Post(() => ApplyTheme(vm.IsDarkMode));
        }
    }

    /// <summary>
    /// Swaps the runtime brush values backing the view's <c>UserControl.Resources</c>
    /// dictionary. Avalonia <see cref="StaticResource"/> bindings re-evaluate when
    /// the dictionary entry is replaced, so every panel/border/label that pulls a
    /// VTdls* brush updates automatically. Light = the realistic upstream palette
    /// (white panels, green list headers); Dark = darker variant for use alongside
    /// YAAT's other dark views. The controller toggles via the Facility Menu.
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        if (dark)
        {
            Resources["VTdlsChromeBg"] = new SolidColorBrush(Color.Parse("#0a0a0a"));
            Resources["VTdlsListHeaderBg"] = new SolidColorBrush(Color.Parse("#1c8a1c"));
            Resources["VTdlsListBg"] = new SolidColorBrush(Color.Parse("#181818"));
            Resources["VTdlsListFg"] = new SolidColorBrush(Color.Parse("#5aa9ff"));
            Resources["VTdlsFooterBg"] = new SolidColorBrush(Color.Parse("#181818"));
            Resources["VTdlsFooterFg"] = new SolidColorBrush(Color.Parse("#d8d8d8"));
            Resources["VTdlsFacilityButtonBg"] = new SolidColorBrush(Color.Parse("#1c8a1c"));
            Resources["VTdlsSelectedBg"] = new SolidColorBrush(Color.Parse("#5aa9ff"));
            Resources["VTdlsSelectedFg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsListBorder"] = new SolidColorBrush(Color.Parse("#3a3a3a"));
            Resources["VTdlsEditorTextFg"] = new SolidColorBrush(Color.Parse("#d8d8d8"));
            // Hover: slightly lighter than list bg so the highlight reads;
            // keep the same blue foreground so text stays legible against it.
            Resources["VTdlsHoverBg"] = new SolidColorBrush(Color.Parse("#2a2a2a"));
            Resources["VTdlsHoverFg"] = new SolidColorBrush(Color.Parse("#8ec5ff"));
        }
        else
        {
            Resources["VTdlsChromeBg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsListHeaderBg"] = new SolidColorBrush(Color.Parse("#1ec51e"));
            Resources["VTdlsListBg"] = new SolidColorBrush(Color.Parse("#ffffff"));
            Resources["VTdlsListFg"] = new SolidColorBrush(Color.Parse("#0000bb"));
            Resources["VTdlsFooterBg"] = new SolidColorBrush(Color.Parse("#f0f0f0"));
            Resources["VTdlsFooterFg"] = new SolidColorBrush(Color.Parse("#202020"));
            Resources["VTdlsFacilityButtonBg"] = new SolidColorBrush(Color.Parse("#1ec51e"));
            Resources["VTdlsSelectedBg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsSelectedFg"] = new SolidColorBrush(Color.Parse("#ffffff"));
            Resources["VTdlsListBorder"] = new SolidColorBrush(Color.Parse("#a0a0a0"));
            Resources["VTdlsEditorTextFg"] = new SolidColorBrush(Color.Parse("#000000"));
            // Hover: lighter shade than the white list bg so it still reads
            // as a highlight against the white surface.
            Resources["VTdlsHoverBg"] = new SolidColorBrush(Color.Parse("#e0e0e8"));
            Resources["VTdlsHoverFg"] = new SolidColorBrush(Color.Parse("#0000bb"));
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, TickClock);
        _clockTimer.Start();
        TickClock(null, EventArgs.Empty);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _clockTimer?.Stop();
        _clockTimer = null;
    }

    private void TickClock(object? sender, EventArgs e)
    {
        // Upstream renders only one clock (footer, HH:MM). We render seconds too
        // so the controller can verify the page is live; the format still fits
        // the same footer slot.
        var now = DateTime.UtcNow;
        FooterZuluClock.Text = now.ToString(@"HH\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void OnFacilityButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VTdlsViewModel vm)
        {
            return;
        }

        // Refresh the accessible-facility list on click so a long-running window
        // picks up new TDLS facilities that came online mid-session.
        await vm.RefreshAccessibleFacilitiesAsync();

        var flyout = new MenuFlyout();
        foreach (var facility in vm.AccessibleFacilities)
        {
            var item = new MenuItem { Header = $"{facility.FacilityId} — {facility.FacilityName}" };
            var capturedId = facility.FacilityId;
            item.Click += async (_, _) => await vm.SwitchFacilityAsync(capturedId);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(no TDLS facilities accessible)", IsEnabled = false });
        }

        // Upstream's Facility Menu hangs the Dark Mode toggle below the
        // facility-switcher entries — same layout here. Checkable so the user
        // sees the current state at a glance.
        flyout.Items.Add(new Separator());
        var darkModeItem = new MenuItem
        {
            Header = "Dark Mode",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = vm.IsDarkMode,
        };
        darkModeItem.Click += (_, _) => vm.IsDarkMode = !vm.IsDarkMode;
        flyout.Items.Add(darkModeItem);

        flyout.ShowAt(FacilityButton);
    }

    private async void OnDumpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VTdlsViewModel vm)
        {
            await vm.DumpSelectedCommand.ExecuteAsync(null);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // Upstream's Cancel button closes the editor without sending. Mirror
        // the F10 key handler — clearing SelectedItem dismisses the editor.
        if (DataContext is VTdlsViewModel vm)
        {
            vm.SelectedItem = null;
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not VTdlsViewModel vm)
        {
            return;
        }

        // In-view Find (Ctrl+F / F3 / Shift+F3 / Esc) takes priority over TDLS shortcuts.
        if (HandleFindKeys(e))
        {
            return;
        }

        // While the find box owns keyboard focus, its editing keys must not trigger TDLS actions.
        if (_findController.IsVisible && FindBar.IsKeyboardFocusWithin)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F4:
                e.Handled = true;
                await vm.DumpSelectedCommand.ExecuteAsync(null);
                break;
            case Key.F12:
                if (vm.Editor is { IsSendEnabled: true } editor)
                {
                    e.Handled = true;
                    await editor.SendCommand.ExecuteAsync(null);
                }
                break;
            case Key.F10:
                if (vm.Editor is not null)
                {
                    e.Handled = true;
                    vm.SelectedItem = null;
                }
                break;
            default:
                break;
        }
    }
}

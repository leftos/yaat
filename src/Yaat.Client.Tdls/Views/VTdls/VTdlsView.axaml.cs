using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
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

    public VTdlsView()
    {
        InitializeComponent();

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        KeyDown += OnKeyDown;
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

        // Footer status — drives the "MANDATORY FIELD NOT SET" message upstream
        // shows when the controller has an editor open with missing fields.
        if (DataContext is VTdlsViewModel { Editor: { } editor })
        {
            FooterStatus.Text = editor.IsSendEnabled ? "CLEARANCE TYPE: PDC" : $"MANDATORY FIELD NOT SET — {editor.MissingMandatoryFieldNames}";
            FooterStatus.Foreground = editor.IsSendEnabled ? Brushes.White : Brushes.OrangeRed;
        }
        else
        {
            FooterStatus.Text = "CLEARANCE TYPE: PDC";
            FooterStatus.Foreground = Brushes.White;
        }
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

        flyout.ShowAt(FacilityButton);
    }

    private async void OnDumpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VTdlsViewModel vm)
        {
            await vm.DumpSelectedCommand.ExecuteAsync(null);
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not VTdlsViewModel vm)
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

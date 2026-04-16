using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.VStrips;

/// <summary>
/// Code-behind for the vStrips view. Handles user input that can't be expressed
/// cleanly in XAML: bay selection clicks, drag/drop of strips between racks and
/// onto the trash zone, and the keyboard shortcut map from docs/crc/vstrips.md.
///
/// Every user action funnels through <see cref="VStripsViewModel"/> helpers
/// which emit canonical commands — the view never mutates strip state directly.
/// </summary>
public partial class VStripsView : UserControl
{
    // Application-scoped data format for drag/drop. Strip ids fit in a string so
    // the drop target can resolve the view-model through VStripsViewModel.ItemsById
    // without marshalling anything across the drag boundary.
    private static readonly DataFormat<string> StripIdFormat = DataFormat.CreateStringApplicationFormat("yaat.strip-id");

    public VStripsView()
    {
        InitializeComponent();

        // Initialize drag/drop on the trash zone once — bay buttons and rack
        // content get wired up lazily from OnAttachedToVisualTree because
        // ItemsControl hosts need their child containers realized first.
        var trash = this.FindControl<Border>("TrashZone");
        if (trash is not null)
        {
            DragDrop.SetAllowDrop(trash, true);
            trash.AddHandler(DragDrop.DragOverEvent, OnTrashDragOver);
            trash.AddHandler(DragDrop.DropEvent, OnTrashDrop);
        }

        // Drag-source wiring for individual strip item views — listened at the
        // UserControl level so dynamically-created strips all get it.
        AddHandler(PointerPressedEvent, OnStripPointerPressed, RoutingStrategies.Tunnel);

        // Drop targets on rack strip lists — also handled at the UserControl
        // level so racks created after load still receive drops.
        AddHandler(DragDrop.DragOverEvent, OnGenericDragOver);
        AddHandler(DragDrop.DropEvent, OnRackDrop);
    }

    // ── Bay selection ───────────────────────────────────────────

    private async void OnBayButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StripBayViewModel bay } && DataContext is VStripsViewModel vm)
        {
            await vm.SelectBayAsync(bay);
        }
    }

    // ── Drag source ─────────────────────────────────────────────

    private async void OnStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Walk up from the click target to find the StripItemView under the
        // cursor. StripItemView's Tag is the StripItemViewModel (bound in the
        // rack/printer ItemTemplate).
        if (e.Source is not Visual hit)
        {
            return;
        }

        var stripView = hit.FindAncestorOfType<StripItemView>();
        if (stripView?.Tag is not StripItemViewModel strip || DataContext is not VStripsViewModel vm)
        {
            return;
        }

        vm.SelectedStrip = strip;

        // Shift+click toggles offset without starting a drag.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            await vm.ToggleOffsetAsync(strip);
            e.Handled = true;
            return;
        }

        // Alt+click deletes the strip.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            await vm.DeleteStripAsync(strip);
            e.Handled = true;
            return;
        }

        // Start a drag — ignored if the user just clicks without moving.
        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(StripIdFormat, strip.Id));
        await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Move);
    }

    // ── Rack drop target ────────────────────────────────────────

    private void OnGenericDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer?.Contains(StripIdFormat) == true)
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnRackDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not VStripsViewModel vm || e.DataTransfer is null)
        {
            return;
        }

        var stripId = e.DataTransfer.TryGetValue(StripIdFormat);
        if (stripId is null || !vm.ItemsById.TryGetValue(stripId, out var strip))
        {
            return;
        }

        // Walk up from the drop target to find the rack's ItemsControl (Tag =
        // StripRackViewModel). The owning bay is derived from the VM's Bays
        // collection since we only show SelectedBay in the main panel.
        var hit = e.Source as Visual;
        var rackHost = hit?.FindAncestorOfType<ItemsControl>();
        while (rackHost is not null && rackHost.Tag is not StripRackViewModel)
        {
            rackHost = rackHost.GetVisualParent()?.FindAncestorOfType<ItemsControl>();
        }

        if (rackHost?.Tag is not StripRackViewModel rack || vm.SelectedBay is null)
        {
            return;
        }

        // Compute the insertion index from pointer Y relative to the rack's
        // strip list. Strips stack vertically so dividing by strip height gives
        // the drop slot.
        var pos = e.GetPosition(rackHost);
        var stripHeight = rackHost.Bounds.Height > 0 && rack.Strips.Count > 0 ? rackHost.Bounds.Height / rack.Strips.Count : 40.0;
        var index = stripHeight > 0 ? (int)Math.Clamp(pos.Y / stripHeight, 0, rack.Strips.Count) : 0;

        await vm.MoveStripAsync(strip, vm.SelectedBay, rack.RackIndex, index);
        e.Handled = true;
    }

    // ── Trash drop target ───────────────────────────────────────

    private void OnTrashDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer?.Contains(StripIdFormat) == true ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTrashDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not VStripsViewModel vm || e.DataTransfer is null)
        {
            return;
        }

        var stripId = e.DataTransfer.TryGetValue(StripIdFormat);
        if (stripId is null || !vm.ItemsById.TryGetValue(stripId, out var strip))
        {
            return;
        }

        await vm.DeleteStripAsync(strip);
        e.Handled = true;
    }

    // ── Keyboard shortcuts ──────────────────────────────────────

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not VStripsViewModel vm)
        {
            base.OnKeyDown(e);
            return;
        }

        // Bay cycling.
        if (e.Key == Key.PageDown)
        {
            await vm.NextBayAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageUp)
        {
            await vm.PreviousBayAsync();
            e.Handled = true;
            return;
        }

        // Tab / Esc toggle the printer panel.
        if (e.Key is Key.Tab or Key.Escape)
        {
            vm.Printer.IsOpen = !vm.Printer.IsOpen;
            e.Handled = true;
            return;
        }

        // Delete / Backspace: delete the selected strip.
        if (e.Key is Key.Delete or Key.Back)
        {
            if (vm.SelectedStrip is { } sel)
            {
                await vm.DeleteStripAsync(sel);
                e.Handled = true;
            }
            return;
        }

        // Ctrl+Alt+1..9: jump to bay by ordinal (matches vstrips.md).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var bayIdx = e.Key switch
            {
                Key.D1 => 0,
                Key.D2 => 1,
                Key.D3 => 2,
                Key.D4 => 3,
                Key.D5 => 4,
                Key.D6 => 5,
                Key.D7 => 6,
                Key.D8 => 7,
                Key.D9 => 8,
                _ => -1,
            };
            if (bayIdx >= 0 && bayIdx < vm.Bays.Count)
            {
                await vm.SelectBayAsync(vm.Bays[bayIdx]);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }
}

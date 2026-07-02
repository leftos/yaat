using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.UI.Tests.Views;

// Regression for GitHub issue #229: the ground-map right-click "Cleared for takeoff"
// menu item sent "CTO 28R", which the server rejects ("CTO does not understand '28R'")
// because CTO has no runway argument — the runway is resolved server-side from the
// aircraft's assigned runway. The same defect affected the sibling "Line up and wait"
// item ("LUAW 28R"). Both must send the bare verb; the runway belongs in the label only.
public class Issue229TakeoffMenuRunwayTests
{
    private static (GroundViewModel Vm, Func<string?> LastCommand) BuildVm()
    {
        string? lastCommand = null;
        var vm = new GroundViewModel(
            new ServerConnection(),
            sendCommand: (_, command, _) =>
            {
                lastCommand = command;
                return Task.CompletedTask;
            }
        );
        return (vm, () => lastCommand);
    }

    private static ContextMenu BuildHoldShortMenu(GroundViewModel vm)
    {
        var ac = new AircraftModel { Callsign = "EJA921", CurrentPhase = "Holding Short 28R/10L" };
        var menu = new ContextMenu();
        GroundView.AddHoldShortCrossingItems(menu, vm, ac, "Holding Short 28R/10L", "EJA921", "GG");
        return menu;
    }

    private static MenuItem FindItem(ItemCollection items, string header) =>
        items.OfType<MenuItem>().Single(m => m.Header is string s && s == header);

    [AvaloniaFact]
    public void ClearedForTakeoffDefault_SendsBareCto_NotRunwayArgument()
    {
        var (vm, lastCommand) = BuildVm();
        var menu = BuildHoldShortMenu(vm);

        var ctoParent = FindItem(menu.Items, "Cleared for takeoff 28R");
        var defaultItem = FindItem(ctoParent.Items, "Default (SID/on course)");
        defaultItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal("CTO", lastCommand());
    }

    [AvaloniaFact]
    public void LineUpAndWait_SendsBareLuaw_NotRunwayArgument()
    {
        var (vm, lastCommand) = BuildVm();
        var menu = BuildHoldShortMenu(vm);

        var luawItem = FindItem(menu.Items, "Line up and wait 28R");
        luawItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal("LUAW", lastCommand());
    }
}

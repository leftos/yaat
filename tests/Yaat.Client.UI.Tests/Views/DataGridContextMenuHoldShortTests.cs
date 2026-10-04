using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the track-list (DataGrid) right-click menu deriving its "Cross"/"Line up
// and wait" entries from AssignedRunway (the departure runway) instead of the runway being
// held. N784ME holding short of 15/33 with a 28R departure must be offered to cross 15, not 28R.
public class DataGridContextMenuHoldShortTests
{
    [AvaloniaFact]
    public void HoldingShort_CrossesHeldRunway_NotAssignedRunway()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var ac = new AircraftModel
        {
            Callsign = "N784ME",
            IsOnGround = true,
            CurrentPhase = "Holding Short 15/33",
            AssignedRunway = "28R",
        };

        vm.Aircraft.Add(ac);
        ContextMenu menu = DataGridView.BuildAircraftMenu(vm, new DataGrid(), ac, null, [ac]);
        List<string> headers = HeadersIn(menu.Items);

        Assert.Equal(["Cross 15"], headers.Where(h => h.StartsWith("Cross ", StringComparison.Ordinal)));
        Assert.DoesNotContain(headers, h => h.Contains("28R", StringComparison.Ordinal));
    }

    /// <summary>Every item header in <paramref name="items"/>' whole tree, submenus included, depth first.</summary>
    private static List<string> HeadersIn(ItemCollection items) =>
        [.. items.OfType<MenuItem>().SelectMany(m => (m.Header is string header ? [header] : Array.Empty<string>()).Concat(HeadersIn(m.Items)))];
}

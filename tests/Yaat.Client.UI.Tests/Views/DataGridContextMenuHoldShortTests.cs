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

        var menu = new ContextMenu();
        DataGridView.AddPhaseAwareItems(menu, ac, vm, "N784ME", "AB");

        var crossItems = menu
            .Items.OfType<MenuItem>()
            .Where(m => m.Header is string s && s.StartsWith("Cross ", StringComparison.Ordinal))
            .Select(m => (string)m.Header!)
            .ToList();

        Assert.Equal(new[] { "Cross 15" }, crossItems);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header is string s && s.Contains("28R", StringComparison.Ordinal));
    }
}

using System.Linq;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

public class MainViewModelCommandHistoryTests
{
    [AvaloniaFact]
    public void AddHistory_UppercasesAndMovesCaseInsensitiveDuplicateToFront()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.AddHistory("", "fh 270");
        vm.AddHistory("", "cland");
        vm.AddHistory("", "CLAND");

        Assert.Equal(["CLAND", "FH 270"], vm.CommandHistory.Select(e => e.Command));
    }

    [AvaloniaFact]
    public void AddHistory_SameCommandDifferentAircraft_KeepsBothEntries()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.AddHistory("UAL1", "FH 090");
        vm.AddHistory("AAL2", "FH 090");

        Assert.Equal([new CommandHistoryEntry("AAL2", "FH 090"), new CommandHistoryEntry("UAL1", "FH 090")], vm.CommandHistory);
    }

    [AvaloniaFact]
    public void GetRecallHistory_WithSelection_ReturnsSelectedAircraftAndGlobalCommands()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var ual = new AircraftModel { Callsign = "UAL1" };
        vm.Aircraft.Add(ual);
        vm.Aircraft.Add(new AircraftModel { Callsign = "AAL2" });

        vm.AddHistory("UAL1", "FH 090");
        vm.AddHistory("AAL2", "TL 250");
        vm.AddHistory("", "PAUSE");

        vm.SelectedAircraft = ual;

        // Newest-first, UAL1's command plus the untargeted global command; AAL2's is filtered out.
        Assert.Equal(["PAUSE", "FH 090"], vm.GetRecallHistory());
    }

    [AvaloniaFact]
    public void GetRecallHistory_NoSelection_ReturnsAllCommands()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.AddHistory("UAL1", "FH 090");
        vm.AddHistory("AAL2", "TL 250");
        vm.AddHistory("", "PAUSE");

        vm.SelectedAircraft = null;

        Assert.Equal(["PAUSE", "TL 250", "FH 090"], vm.GetRecallHistory());
    }
}

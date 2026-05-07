using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

public class MainViewModelCommandHistoryTests
{
    [AvaloniaFact]
    public void AddHistory_UppercasesAndMovesCaseInsensitiveDuplicateToFront()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.AddHistory("fh 270");
        vm.AddHistory("cland");
        vm.AddHistory("CLAND");

        Assert.Equal(["CLAND", "FH 270"], vm.CommandHistory);
    }
}

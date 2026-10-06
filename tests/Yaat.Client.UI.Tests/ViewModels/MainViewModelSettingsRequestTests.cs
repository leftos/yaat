using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// <see cref="MainViewModel.RequestSettings"/> raises <see cref="MainViewModel.SettingsRequested"/> once, carrying the
/// section to open on.
/// </summary>
public class MainViewModelSettingsRequestTests
{
    [AvaloniaFact]
    public void RequestSettings_RaisesSettingsRequestedOnce_WithTheSection()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;

        vm.RequestSettings(SettingsSectionId.Radar);

        Assert.Equal([SettingsSectionId.Radar], requests);
    }
}

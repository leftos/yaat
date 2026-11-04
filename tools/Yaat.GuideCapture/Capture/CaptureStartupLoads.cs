using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.GuideCapture.Capture;

// The startup loads every capture window's MainViewModel runs. Program loads the
// navigation database once before any window opens, so each view model finds it
// ready at construction and never races its own load against the scenario
// bootstrap (a lost race centres the radar on the ARTCC area, not the airport).
// The vNAS configuration still loads per view model; the update check is off.
internal sealed class CaptureStartupLoads : IMainViewModelStartupLoads
{
    public Task<bool> EnsureNavigationDatabaseAsync() => Task.FromResult(true);

    public Task InitializeVnasConfigAsync(VnasConfigService service) => service.InitializeAsync();

    public bool RunsStartupUpdateCheck => false;
}

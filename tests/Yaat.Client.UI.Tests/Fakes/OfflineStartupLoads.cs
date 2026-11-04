using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Fakes;

/// <summary>
/// Startup loads for the view models a UI test builds: the committed test navdata from the pins <see cref="ModuleInit"/>
/// resolves, and no vNAS configuration request and no update check.
/// </summary>
internal sealed class OfflineStartupLoads : IMainViewModelStartupLoads
{
    /// <summary>
    /// Completes off the constructor, as the app's own load does: a test that never yields to the dispatcher sees the nav
    /// db still loading, and one that awaits <see cref="MainViewModel.NavDataInitialization"/> sees it ready. Every view
    /// model re-installs the committed database (a parse once per process, then a set of the cached copy), since a test
    /// may have replaced the singleton with a synthetic one and left it there.
    /// </summary>
    public Task<bool> EnsureNavigationDatabaseAsync() =>
        Task.Run(() =>
        {
            TestVnasData.EnsureInitialized();
            return TestVnasData.NavigationDb is not null;
        });

    public Task InitializeVnasConfigAsync(VnasConfigService service) => Task.CompletedTask;

    public bool RunsStartupUpdateCheck => false;
}

using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.Fakes;

/// <summary>
/// The committed navigation data (<see cref="OfflineStartupLoads"/>), finishing its load only when the test opens
/// <see cref="Gate"/>: a test that asserts what a view model shows while the navigation data is still loading holds it there.
/// </summary>
internal sealed class GatedStartupLoads : IMainViewModelStartupLoads
{
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> EnsureNavigationDatabaseAsync()
    {
        await Gate.Task;
        return await new OfflineStartupLoads().EnsureNavigationDatabaseAsync();
    }

    public Task InitializeVnasConfigAsync(VnasConfigService service) => Task.CompletedTask;

    public bool RunsStartupUpdateCheck => false;

    /// <summary>
    /// A main view model whose navigation-data load waits on a fresh gate. <see cref="MainViewModel.StartupLoads"/> is read
    /// once per constructor and put back before returning, so no other test's view model sees the gate.
    /// </summary>
    public static (MainViewModel Vm, GatedStartupLoads Loads) Create()
    {
        var loads = new GatedStartupLoads();
        IMainViewModelStartupLoads previous = MainViewModel.StartupLoads;
        MainViewModel.StartupLoads = loads;
        try
        {
            return (new MainViewModel(new FakeFilePickerService()), loads);
        }
        finally
        {
            MainViewModel.StartupLoads = previous;
        }
    }
}

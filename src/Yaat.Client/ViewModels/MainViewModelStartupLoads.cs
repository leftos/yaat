using Yaat.Client.Services;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The loads a <see cref="MainViewModel"/> starts from its constructor: the navigation database, the vNAS configuration
/// and the delayed startup update check. A test host swaps in loads that stay offline, so the view models it builds send
/// no request and parse no NavData.dat of their own.
/// </summary>
internal interface IMainViewModelStartupLoads
{
    /// <summary>
    /// Makes <see cref="NavigationDatabase.Instance"/> usable. Exceptions propagate to the caller.
    /// </summary>
    /// <returns><c>true</c> when the database is ready; <c>false</c> when NavData or CIFP data is unavailable.</returns>
    Task<bool> EnsureNavigationDatabaseAsync();

    /// <summary>Loads the vNAS configuration into <paramref name="service"/>.</summary>
    Task InitializeVnasConfigAsync(VnasConfigService service);

    /// <summary>Whether the constructor starts the delayed startup update check.</summary>
    bool RunsStartupUpdateCheck { get; }
}

/// <summary>The app's own startup loads: vNAS NavData, CIFP, the vNAS configuration and the update check.</summary>
internal sealed class DefaultMainViewModelStartupLoads : IMainViewModelStartupLoads
{
    public async Task<bool> EnsureNavigationDatabaseAsync()
    {
        using var vnasData = new VnasDataService();
        await vnasData.InitializeAsync();

        using var cifpService = new CifpDataService();
        await cifpService.InitializeAsync(CifpDataService.CreateDefaultOptions());

        if (vnasData.NavData is null || cifpService.CifpFilePath is null)
        {
            return false;
        }

        NavigationDatabase.Initialize(vnasData.NavData, cifpService.CifpFilePath, supplementaryCifpFilePaths: cifpService.SupplementaryCifpFilePaths);
        return true;
    }

    public Task InitializeVnasConfigAsync(VnasConfigService service) => service.InitializeAsync();

    public bool RunsStartupUpdateCheck => true;
}

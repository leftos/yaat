using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests;

// Only Yaat.Client.UI.Tests compiles this part; the assemblies that link ModuleInit.cs alone build no MainViewModel.
internal static partial class ModuleInit
{
    // A view model a test builds runs offline, and its primary Scope runs with no pref-set or profile files.
    static partial void InstallStartupLoads()
    {
        MainViewModel.StartupLoads = new OfflineStartupLoads();
        MainViewModel.ScopeStoreFactory = () => (null, null);
    }
}

using System.Diagnostics.CodeAnalysis;
using Yaat.Sim.Testing;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Loads the navdata the sandbox modes resolve fixes and routes against: the real data the sim tests
/// use, committed under <c>tests/Yaat.Sim.Tests/TestData</c> in the checkout the sandbox runs from.
/// </summary>
internal static class SandboxNavData
{
    /// <summary>Initializes the navdata; false with the reason when the sandbox is not inside a yaat checkout.</summary>
    public static bool TryInitialize([NotNullWhen(false)] out string? error)
    {
        string? repoRoot = OuroborosRunner.TryFindRepoRoot();
        if (repoRoot is null)
        {
            error = "must run inside a yaat checkout (needs tests/Yaat.Sim.Tests/TestData)";
            return false;
        }
        TestVnasData.SetTestDataDir(Path.Combine(repoRoot, "tests", "Yaat.Sim.Tests", "TestData"));
        TestVnasData.EnsureInitialized();
        error = null;
        return true;
    }
}

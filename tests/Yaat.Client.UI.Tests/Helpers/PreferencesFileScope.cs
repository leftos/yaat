using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>
/// Puts the per-process <c>preferences.json</c> back as it was when the scope opened, so a test that commits
/// settings leaves no trace for the tests after it.
/// </summary>
internal sealed class PreferencesFileScope : IDisposable
{
    private static readonly string PreferencesPath = YaatPaths.Combine("preferences.json");

    private readonly string? _original = File.Exists(PreferencesPath) ? File.ReadAllText(PreferencesPath) : null;

    public void Dispose()
    {
        if (_original is null)
        {
            File.Delete(PreferencesPath);
            return;
        }

        File.WriteAllText(PreferencesPath, _original);
    }
}

using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesDepartureAutoDeleteTests
{
    private static readonly string PreferencesPath = YaatPaths.Combine("preferences.json");

    /// <summary>
    /// A preferences.json written before the field existed carries no key for it; the loader reads null (departures kept)
    /// without a migration step.
    /// </summary>
    [Fact]
    public void FileWithoutTheKey_ReadsNull()
    {
        WithPreferencesFile(
            """{"preferencesVersion": 1, "autoDeleteOverride": "Parked"}""",
            () =>
            {
                var prefs = new UserPreferences();

                Assert.Null(prefs.DepartureAutoDeleteDistanceNm);
                Assert.Equal("Parked", prefs.AutoDeleteOverride);
            }
        );
    }

    [Fact]
    public void SetDepartureAutoDeleteDistanceNm_Persists_AndNullClearsIt()
    {
        WithPreferencesFile(
            null,
            () =>
            {
                var prefs = new UserPreferences();
                Assert.Null(prefs.DepartureAutoDeleteDistanceNm);

                prefs.SetDepartureAutoDeleteDistanceNm(30);
                Assert.Equal(30, new UserPreferences().DepartureAutoDeleteDistanceNm);

                prefs.SetDepartureAutoDeleteDistanceNm(null);
                Assert.Null(new UserPreferences().DepartureAutoDeleteDistanceNm);
            }
        );
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a preferences.json holding exactly <paramref name="json"/> (no file when it is
    /// null), then puts back whatever was there before — the tests share one per-process preferences.json.
    /// </summary>
    private static void WithPreferencesFile(string? json, Action body)
    {
        string? original = File.Exists(PreferencesPath) ? File.ReadAllText(PreferencesPath) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            if (json is null)
            {
                File.Delete(PreferencesPath);
            }
            else
            {
                File.WriteAllText(PreferencesPath, json);
            }
            body();
        }
        finally
        {
            if (original is null)
            {
                File.Delete(PreferencesPath);
            }
            else
            {
                File.WriteAllText(PreferencesPath, original);
            }
        }
    }
}

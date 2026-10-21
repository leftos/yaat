using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesDepartureAutoDeleteTests
{
    private static string PreferencesPath => UserPreferences.FilePath;

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
    /// null).
    /// </summary>
    private static void WithPreferencesFile(string? json, Action body)
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
}

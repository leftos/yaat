using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The preferences entry of a settings bundle carries only the allowlisted Settings fields: never the admin password,
/// servers, folders, devices or window geometry, and every preference key is placed in the allowlist or the named
/// exclusion list. These tests use the per-process preferences.json and touch only fields no other test here reads.
/// </summary>
public class SettingsBundlePreferencesTests
{
    [Fact]
    public void PreferencesBundle_NeverContainsExcludedFields()
    {
        var preferences = new UserPreferences();
        preferences.SetAdminSettings(isAdmin: true, "bundle-secret-password");
        preferences.SetSavedServers([new SavedServer("Bundle Test", "https://bundle-test-server.example")], "https://bundle-test-server.example");
        preferences.SetLastScenarioFolder(@"C:\bundle-test-scenarios");
        preferences.SetCrcAliasDirectory(@"C:\bundle-test-aliases");
        preferences.SetAudioSettings("Bundle Test Microphone", "Bundle Test Speakers");
        preferences.SetWindowGeometry(
            "Main",
            new SavedWindowGeometry
            {
                X = 4321,
                Y = 1234,
                Width = 999,
                Height = 777,
            }
        );

        using var buffer = new MemoryStream();
        SettingsBundleFile.Write([SettingsBundleItems.Preferences(preferences)], "YAAT test", buffer);
        string text = AllEntryText(buffer.ToArray());

        string[] forbidden =
        [
            "bundle-secret-password",
            "bundle-test-server",
            "bundle-test-scenarios",
            "bundle-test-aliases",
            "Bundle Test Microphone",
            "Bundle Test Speakers",
            "4321",
            "adminPassword",
            "isAdminMode",
            "savedServers",
            "lastUsedServerUrl",
            "lastScenarioFolder",
            "crcAliasDirectory",
            "audioInputDevice",
            "audioOutputDevice",
            "mainWindowGeometry",
            "windowGeometries",
        ];
        Assert.All(forbidden, value => Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("showAtpa", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySettingsSectionPreference_IsAllowlistedOrNamedExcluded()
    {
        IReadOnlyList<string> all = UserPreferences.PreferenceKeys;
        IReadOnlyList<string> bundled = UserPreferences.BundledPreferenceKeys;
        IReadOnlyDictionary<string, string> unbundled = UserPreferences.UnbundledPreferenceKeys;

        // Materialized so a failure lists the offending keys.
        List<string> unplaced = [.. all.Where(key => !bundled.Contains(key) && !unbundled.ContainsKey(key))];
        List<string> placedTwice = [.. bundled.Where(unbundled.ContainsKey)];
        List<string> notPreferences = [.. bundled.Concat(unbundled.Keys).Where(key => !all.Contains(key))];

        Assert.Empty(unplaced);
        Assert.Empty(placedTwice);
        Assert.Empty(notPreferences);
        Assert.Equal(bundled.Count, bundled.Distinct(StringComparer.Ordinal).Count());
        Assert.All(unbundled, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    [Fact]
    public void PreferencesImport_AppliesAllowlistedKeys_AndIgnoresEverythingElse()
    {
        var preferences = new UserPreferences();
        bool atpaBefore = preferences.ShowAtpa;
        string passwordBefore = preferences.AdminPassword;
        string modelBefore = preferences.LlmModelPath;
        var incoming = new JsonObject
        {
            ["showAtpa"] = !atpaBefore,
            ["adminPassword"] = "imported-secret",
            ["llmModelPath"] = @"C:\models\local.gguf",
            ["euroScopeMode"] = "not a boolean",
            ["noSuchPreference"] = 1,
        };

        PreferencesImportResult result = preferences.ImportBundlePreferences(incoming);

        Assert.Equal(["showAtpa"], result.AppliedKeys);
        Assert.Equal(["adminPassword", "llmModelPath", "euroScopeMode", "noSuchPreference"], result.IgnoredKeys);
        Assert.Equal(!atpaBefore, preferences.ShowAtpa);
        Assert.Equal(passwordBefore, preferences.AdminPassword);
        Assert.Equal(modelBefore, preferences.LlmModelPath);
        preferences.SetShowAtpa(atpaBefore);
    }

    private static string AllEntryText(byte[] bundle)
    {
        using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        var text = new List<string>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            text.Add(reader.ReadToEnd());
        }

        return string.Join("\n", text);
    }
}

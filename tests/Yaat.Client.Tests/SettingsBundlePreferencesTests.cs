using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;

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
    public void ShowQuickCommandIcons_DefaultsOn_AndRoundTripsThroughTheBundle()
    {
        var preferences = new UserPreferences();
        Assert.True(preferences.ShowQuickCommandIcons);
        Assert.Contains("showQuickCommandIcons", UserPreferences.BundledPreferenceKeys);
        Assert.True(preferences.ExportBundlePreferences()["showQuickCommandIcons"]!.GetValue<bool>());

        try
        {
            PreferencesImportResult result = preferences.ImportBundlePreferences(new JsonObject { ["showQuickCommandIcons"] = false });

            Assert.Equal(["showQuickCommandIcons"], result.AppliedKeys);
            Assert.False(preferences.ShowQuickCommandIcons);
            Assert.False(preferences.ExportBundlePreferences()["showQuickCommandIcons"]!.GetValue<bool>());
            Assert.False(new UserPreferences().ShowQuickCommandIcons);
        }
        finally
        {
            preferences.SetShowQuickCommandIcons(true);
        }
    }

    [Fact]
    public void ScopeText_DefaultsToCompact()
    {
        var defaults = UserPreferences.CreateDefaults();
        Assert.Equal(ScopeTextStyle.Compact, defaults.StarsScopeTextStyle);
        Assert.Equal(2, defaults.StarsScopeTextSize);
        Assert.Equal(ScopeTextStyle.Compact, defaults.EramScopeTextStyle);
        Assert.Equal(1, defaults.EramScopeTextSize);
    }

    [Fact]
    public void ScopeText_RoundTripsThroughTheBundle_TellingListenersOnce()
    {
        var preferences = new UserPreferences();
        (ScopeTextStyle StarsStyle, int StarsSize, ScopeTextStyle EramStyle, int EramSize) before = ScopeTextOf(preferences);
        int raised = 0;
        preferences.ScopeTextChanged += () => raised++;
        try
        {
            preferences.SetScopeText(ScopeTextStyle.Compact, 0, ScopeTextStyle.Compact, 0);
            raised = 0;
            PreferencesImportResult result = preferences.ImportBundlePreferences(
                new JsonObject
                {
                    ["starsScopeTextStyle"] = "Realistic",
                    ["starsScopeTextSize"] = 6,
                    ["eramScopeTextStyle"] = "Realistic",
                    ["eramScopeTextSize"] = 4,
                }
            );

            Assert.Equal(["starsScopeTextStyle", "starsScopeTextSize", "eramScopeTextStyle", "eramScopeTextSize"], result.AppliedKeys);
            Assert.Equal(1, raised);
            var reloaded = new UserPreferences();
            Assert.Equal(ScopeTextStyle.Realistic, reloaded.StarsScopeTextStyle);
            Assert.Equal(6, reloaded.StarsScopeTextSize);
            Assert.Equal(ScopeTextStyle.Realistic, reloaded.EramScopeTextStyle);
            Assert.Equal(4, reloaded.EramScopeTextSize);

            JsonObject exported = preferences.ExportBundlePreferences();
            Assert.Equal("Realistic", exported["starsScopeTextStyle"]!.GetValue<string>());
            Assert.Equal(6, exported["starsScopeTextSize"]!.GetValue<int>());
            Assert.Equal("Realistic", exported["eramScopeTextStyle"]!.GetValue<string>());
            Assert.Equal(4, exported["eramScopeTextSize"]!.GetValue<int>());

            raised = 0;
            Assert.Equal(["starsScopeTextSize"], preferences.ImportBundlePreferences(new JsonObject { ["starsScopeTextSize"] = 6 }).AppliedKeys);
            Assert.Equal(0, raised);
        }
        finally
        {
            preferences.SetScopeText(before.StarsStyle, before.StarsSize, before.EramStyle, before.EramSize);
        }
    }

    [Fact]
    public void ScopeText_TheBundleIgnoresAnUnknownStyle_AndAnOutOfRangeSize()
    {
        var preferences = new UserPreferences();
        (ScopeTextStyle, int, ScopeTextStyle, int) before = ScopeTextOf(preferences);

        PreferencesImportResult outOfRange = preferences.ImportBundlePreferences(
            new JsonObject
            {
                ["starsScopeTextStyle"] = "Fancy",
                ["starsScopeTextSize"] = 7,
                ["eramScopeTextSize"] = 5,
            }
        );

        Assert.Equal(["starsScopeTextStyle", "starsScopeTextSize", "eramScopeTextSize"], outOfRange.IgnoredKeys);
        Assert.Empty(outOfRange.AppliedKeys);
        Assert.Equal(before, ScopeTextOf(preferences));
    }

    private static (ScopeTextStyle, int, ScopeTextStyle, int) ScopeTextOf(UserPreferences preferences) =>
        (preferences.StarsScopeTextStyle, preferences.StarsScopeTextSize, preferences.EramScopeTextStyle, preferences.EramScopeTextSize);

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

    /// <summary>
    /// A bundle exported before the three-state auto-cross carries the on/off <c>autoCrossRunway</c>: on imports as All runways,
    /// off as Off, and the key reports as applied.
    /// </summary>
    [Theory]
    [InlineData(true, AutoCrossRunwayMode.All)]
    [InlineData(false, AutoCrossRunwayMode.Off)]
    public void AutoCrossRunway_FromABundleBeforeTheMode_ImportsAsTheMode(bool autoCrossRunway, AutoCrossRunwayMode expected)
    {
        var preferences = new UserPreferences();
        AutoCrossRunwayMode before = preferences.AutoCrossRunwayMode;
        try
        {
            preferences.ImportBundlePreferences(new JsonObject { ["autoCrossRunwayMode"] = nameof(AutoCrossRunwayMode.InactiveOnly) });

            PreferencesImportResult result = preferences.ImportBundlePreferences(new JsonObject { ["autoCrossRunway"] = autoCrossRunway });

            Assert.Equal(["autoCrossRunway"], result.AppliedKeys);
            Assert.Empty(result.IgnoredKeys);
            Assert.Equal(expected, new UserPreferences().AutoCrossRunwayMode);
        }
        finally
        {
            preferences.ImportBundlePreferences(new JsonObject { ["autoCrossRunwayMode"] = before.ToString() });
        }
    }

    /// <summary>A bundle carrying both keys imports the mode and ignores the older switch.</summary>
    [Fact]
    public void AutoCrossRunway_BundleWithBothKeys_ImportsTheMode()
    {
        var preferences = new UserPreferences();
        AutoCrossRunwayMode before = preferences.AutoCrossRunwayMode;
        try
        {
            PreferencesImportResult result = preferences.ImportBundlePreferences(
                new JsonObject { ["autoCrossRunway"] = true, ["autoCrossRunwayMode"] = nameof(AutoCrossRunwayMode.InactiveOnly) }
            );

            Assert.Equal(["autoCrossRunwayMode"], result.AppliedKeys);
            Assert.Equal(["autoCrossRunway"], result.IgnoredKeys);
            Assert.Equal(AutoCrossRunwayMode.InactiveOnly, new UserPreferences().AutoCrossRunwayMode);
        }
        finally
        {
            preferences.ImportBundlePreferences(new JsonObject { ["autoCrossRunwayMode"] = before.ToString() });
        }
    }

    [Fact]
    public void ClearTerminalOnScenarioLoad_TravelsInABundle()
    {
        const string Key = "clearTerminalOnScenarioLoad";
        Assert.Contains(Key, UserPreferences.BundledPreferenceKeys);

        var preferences = new UserPreferences();
        bool before = preferences.ClearTerminalOnScenarioLoad;
        try
        {
            Assert.Equal(before, preferences.ExportBundlePreferences()[Key]?.GetValue<bool>());

            PreferencesImportResult result = preferences.ImportBundlePreferences(new JsonObject { [Key] = !before });

            Assert.Equal([Key], result.AppliedKeys);
            Assert.Equal(!before, new UserPreferences().ClearTerminalOnScenarioLoad);
            Assert.Equal([Key], preferences.ImportBundlePreferences(new JsonObject { [Key] = "yes" }).IgnoredKeys);
        }
        finally
        {
            preferences.SetClearTerminalOnScenarioLoad(before);
        }
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

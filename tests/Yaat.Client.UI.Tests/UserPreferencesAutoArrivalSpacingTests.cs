using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesAutoArrivalSpacingTests
{
    [Theory]
    [InlineData("GND", true)]
    [InlineData("TWR", false)]
    [InlineData("APP", false)] // the student is the approach controller — nobody left to do the spacing
    [InlineData("CTR", false)]
    [InlineData("gnd", true)] // case-insensitive
    [InlineData(null, false)] // no student position (an RPO-only room) reads the tower value
    [InlineData("OBS", false)] // an unknown position type reads the tower value
    public void GetAutoArrivalSpacingOnOccupiedRunway_DefaultsByPositionType(string? positionType, bool expected)
    {
        var prefs = new UserPreferences();

        Assert.Equal(expected, prefs.GetAutoArrivalSpacingOnOccupiedRunway(positionType));
    }

    [Fact]
    public void SetSimulationShortcuts_PersistsAutoArrivalSpacingPerType()
    {
        var prefs = new UserPreferences();
        try
        {
            // Invert both defaults so a stale-default read would fail, one at a time so the two keys cannot be
            // satisfied by a single shared field.
            SetAutoArrivalSpacing(prefs, gnd: false, twr: true);

            Assert.False(prefs.GetAutoArrivalSpacingOnOccupiedRunway("GND"));
            Assert.True(prefs.GetAutoArrivalSpacingOnOccupiedRunway("TWR"));

            SetAutoArrivalSpacing(prefs, gnd: false, twr: false);

            // Fresh instance reads preferences.json from disk, proving persistence.
            var reader = new UserPreferences();
            Assert.False(reader.GetAutoArrivalSpacingOnOccupiedRunway("GND"));
            Assert.False(reader.GetAutoArrivalSpacingOnOccupiedRunway("TWR"));
            Assert.False(reader.GetAutoArrivalSpacingOnOccupiedRunway(null));
        }
        finally
        {
            // Tests share one per-process preferences.json; restore factory defaults (GND on, TWR off) so the
            // order-independent defaults test above never reads these inverted values.
            SetAutoArrivalSpacing(prefs, gnd: true, twr: false);
        }
    }

    /// <summary>
    /// A preferences.json written before the file carried a version (≤ 0.13.1, which shipped TWR defaulting on and
    /// saved that default to disk) is migrated once: TWR is reset to off and the file is rewritten at version 1.
    /// </summary>
    [Fact]
    public void UnversionedFile_ResetsTwrToOff_AndIsRewrittenAtVersion1()
    {
        WithPreferencesFile(
            PrefsJson(version: null, gnd: true, twr: true),
            () =>
            {
                var prefs = new UserPreferences();

                Assert.False(prefs.AutoArrivalSpacingOnOccupiedRunwayTwr);
                JsonObject rewritten = JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();
                Assert.Equal(1, rewritten["preferencesVersion"]!.GetValue<int>());
                Assert.False(rewritten["autoArrivalSpacingOnOccupiedRunwayTwr"]!.GetValue<bool>());
            }
        );
    }

    /// <summary>A file already at version 1 is left alone, so a user who turned TWR back on keeps that choice.</summary>
    [Fact]
    public void Version1File_KeepsTwrOn()
    {
        WithPreferencesFile(
            PrefsJson(version: 1, gnd: true, twr: true),
            () => Assert.True(new UserPreferences().AutoArrivalSpacingOnOccupiedRunwayTwr)
        );
    }

    /// <summary>The one-time reset touches TWR only: GND off in an unversioned file stays off.</summary>
    [Fact]
    public void UnversionedFile_LeavesGndUntouched()
    {
        WithPreferencesFile(
            PrefsJson(version: null, gnd: false, twr: true),
            () =>
            {
                var prefs = new UserPreferences();

                Assert.False(prefs.AutoArrivalSpacingOnOccupiedRunwayGnd);
                Assert.False(prefs.AutoArrivalSpacingOnOccupiedRunwayTwr);
            }
        );
    }

    /// <summary>
    /// No preferences.json at all (a first launch) builds the defaults in memory: TWR reads off, and the constructor
    /// writes nothing — the defaults are already at the current version, so there is nothing to migrate.
    /// </summary>
    [Fact]
    public void NoFile_TwrReadsOff_AndTheConstructorCreatesNoFile()
    {
        WithPreferencesFile(
            json: null,
            () =>
            {
                var prefs = new UserPreferences();

                Assert.False(prefs.AutoArrivalSpacingOnOccupiedRunwayTwr);
                Assert.False(File.Exists(PreferencesPath), "constructing preferences with no file on disk wrote one");
            }
        );
    }

    /// <summary>
    /// An unversioned file with one unreadable field fails full deserialization and goes through field-by-field recovery;
    /// the version still reads as 0 there, so the TWR reset runs, the file is rewritten at version 1, and the original is
    /// kept as a <c>.bak</c>.
    /// </summary>
    [Fact]
    public void UnversionedFileWithAnUnreadableField_IsRecovered_ResetsTwr_AndIsRewrittenAtVersion1()
    {
        const string json = """{"savedServers": 5, "autoArrivalSpacingOnOccupiedRunwayGnd": true, "autoArrivalSpacingOnOccupiedRunwayTwr": true}""";
        WithPreferencesFile(
            json,
            () =>
            {
                var prefs = new UserPreferences();

                Assert.False(prefs.AutoArrivalSpacingOnOccupiedRunwayTwr);
                JsonObject rewritten = JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();
                Assert.Equal(1, rewritten["preferencesVersion"]!.GetValue<int>());
                Assert.False(rewritten["autoArrivalSpacingOnOccupiedRunwayTwr"]!.GetValue<bool>());
                Assert.True(File.Exists(BackupPath), "field-by-field recovery did not back up the original file");
            }
        );
    }

    /// <summary>A file written by a newer build (version 2) is left alone: TWR stays on and the file is not rewritten.</summary>
    [Fact]
    public void NewerVersionFile_IsLeftAlone()
    {
        string json = PrefsJson(version: 2, gnd: true, twr: true);
        WithPreferencesFile(
            json,
            () =>
            {
                Assert.True(new UserPreferences().AutoArrivalSpacingOnOccupiedRunwayTwr);
                Assert.Equal(json, File.ReadAllText(PreferencesPath));
            }
        );
    }

    private static readonly string PreferencesPath = YaatPaths.Combine("preferences.json");

    private static readonly string BackupPath = PreferencesPath + ".bak";

    /// <summary>
    /// A preferences.json holding only the two auto arrival spacing keys, plus <c>preferencesVersion</c> when
    /// <paramref name="version"/> is set.
    /// </summary>
    private static string PrefsJson(int? version, bool gnd, bool twr)
    {
        var file = new JsonObject { ["autoArrivalSpacingOnOccupiedRunwayGnd"] = gnd, ["autoArrivalSpacingOnOccupiedRunwayTwr"] = twr };
        if (version is { } v)
        {
            file["preferencesVersion"] = v;
        }
        return file.ToJsonString();
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a preferences.json holding exactly <paramref name="json"/> (no file at all
    /// when it is null) and no <c>.bak</c>, then puts back whatever preferences.json and <c>.bak</c> were there before —
    /// the tests share one per-process preferences.json.
    /// </summary>
    private static void WithPreferencesFile(string? json, Action body)
    {
        string? original = File.Exists(PreferencesPath) ? File.ReadAllText(PreferencesPath) : null;
        string? originalBackup = File.Exists(BackupPath) ? File.ReadAllText(BackupPath) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.Delete(BackupPath);
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
            RestoreFile(PreferencesPath, original);
            RestoreFile(BackupPath, originalBackup);
        }
    }

    private static void RestoreFile(string path, string? contents)
    {
        if (contents is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, contents);
        }
    }

    /// <summary>
    /// Writes the two auto arrival spacing keys through the shared simulation-shortcuts setter, passing every other
    /// shortcut back unchanged so the round-trip cannot disturb the preferences the rest of the suite reads.
    /// </summary>
    private static void SetAutoArrivalSpacing(UserPreferences prefs, bool gnd, bool twr) =>
        prefs.SetSimulationShortcuts(
            prefs.AutoClearedToLandGnd,
            prefs.AutoClearedToLandTwr,
            prefs.AutoClearedToLandApp,
            prefs.AutoClearedToLandCtr,
            prefs.AutoCrossRunway,
            prefs.AutoPullUpToParallel,
            prefs.AutoGoAroundOnOccupiedRunway,
            prefs.AutoRejectTakeoffOnOccupiedRunway,
            gnd,
            twr
        );
}

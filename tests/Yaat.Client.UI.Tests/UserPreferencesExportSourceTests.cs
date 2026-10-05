using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests;

/// <summary>The export source over the user's preferences: what it exports reads back as the data the preferences hold at export time.</summary>
public class UserPreferencesExportSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-importexport-source-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ExportMacros_RoundTripsThroughABundle_WithTheSameMacros()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        preferences.SetMacros([
            new MacroDefinition { Name = "DEP", Expansion = "CTO" },
            new MacroDefinition { Name = "PAT &rwy", Expansion = "ERD &rwy" },
        ]);
        var source = new UserPreferencesExportSource(preferences, NewStore());

        SettingsBundleEntry entry = source.Export(SettingsItemType.Macros);
        using var buffer = new MemoryStream();
        SettingsBundleFile.Write([entry], "test-version", buffer);
        buffer.Position = 0;
        SettingsBundle bundle = SettingsBundleFile.Read("roundtrip" + SettingsBundleFile.Extension, buffer);

        SettingsBundleEntry read = Assert.Single(bundle.Entries);
        Assert.Equal(SettingsItemType.Macros, read.ItemType);
        List<SavedMacro> macros = SettingsBundleItems.ReadMacros(read);
        Assert.Equal([("DEP", "CTO"), ("PAT &rwy", "ERD &rwy")], macros.Select(m => (m.Name, m.Expansion)));
    }

    [Fact]
    public void ExportVerbs_AfterTheSchemeChanges_HasTheNewAliases()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        var source = new UserPreferencesExportSource(preferences, NewStore());

        var scheme = CommandScheme.Default();
        scheme.Patterns[CanonicalCommandType.Timer].Aliases = ["TMRX"];
        preferences.SetCommandScheme(scheme);

        CommandSchemeImport verbs = SettingsBundleItems.ReadVerbs(source.Export(SettingsItemType.Verbs));
        Assert.Equal(["TMRX"], verbs.Verbs[CanonicalCommandType.Timer]);
    }

    private FavoriteStore NewStore() => new(Path.Combine(_root, "favorites-" + Guid.NewGuid().ToString("N")));
}

using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Importing a command-verb file into Settings → Commands edits only the rows the file listed and leaves the
/// rest of the user's scheme alone. Nothing persists until Save, so the assertions read the grid and the
/// scheme it exports rather than preferences.
/// </summary>
public class SettingsViewModelVerbImportTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void ImportVerbs_UpdatesListedRowsOnly()
    {
        var vm = new SettingsViewModel();
        VerbMappingRow untouched = vm.VerbMappings.First(r => r.CommandType == CanonicalCommandType.Speed);
        string untouchedAliases = untouched.Aliases;

        vm.ImportVerbs(
            new CommandSchemeImport(new Dictionary<CanonicalCommandType, List<string>> { [CanonicalCommandType.FlyHeading] = ["HDG"] }, [])
        );

        VerbMappingRow heading = vm.VerbMappings.First(r => r.CommandType == CanonicalCommandType.FlyHeading);
        Assert.Equal("HDG", heading.Aliases);
        Assert.Equal(untouchedAliases, untouched.Aliases);

        CommandScheme exported = vm.ExportVerbs();
        Assert.Equal(["HDG"], exported.Patterns[CanonicalCommandType.FlyHeading].Aliases);
        Assert.Equal(untouchedAliases.Split(',', StringSplitOptions.TrimEntries), exported.Patterns[CanonicalCommandType.Speed].Aliases);
        Assert.False(vm.VerbImportIsError);
        Assert.Contains("1 verb mapping", vm.VerbImportNote, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ImportVerbs_NoteListsUnknownCommands()
    {
        var vm = new SettingsViewModel();

        vm.ImportVerbs(
            new CommandSchemeImport(
                new Dictionary<CanonicalCommandType, List<string>> { [CanonicalCommandType.FlyHeading] = ["HDG"] },
                ["FOO", "BAR"]
            )
        );

        Assert.Contains("FOO, BAR", vm.VerbImportNote, StringComparison.Ordinal);
        Assert.False(vm.VerbImportIsError);
    }
}

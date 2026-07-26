using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="CustomProcedureLoader"/> — indexes ARTCC-supplied CIFP fragments under
/// <c>ARTCCs/{ARTCC}/Procedures/*.cifp</c>. The loader only discovers files and the airports each covers;
/// the records themselves are parsed by <see cref="Yaat.Sim.Data.Vnas.CifpParser"/> unchanged.
/// Warn-don't-throw: an unreadable or record-less file adds a warning and is skipped.
/// </summary>
public class CustomProcedureLoaderTests
{
    /// <summary>A syntactically valid CIFP SID leg long enough to clear the parser's 100-char gate.</summary>
    private static string Record(string icao, char subsection, string procedureId) =>
        ("SUSAP " + icao.PadRight(4) + "K2" + subsection + procedureId.PadRight(6) + "RW28R 010OAK  K2 D 0V").PadRight(132);

    [Fact]
    public void LoadAll_MissingDirectory_ReturnsWarningNoThrow()
    {
        var result = CustomProcedureLoader.LoadAll(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid()));

        Assert.Empty(result.Fragments);
        Assert.Single(result.Warnings);
        Assert.Contains("not found", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAll_NoProceduresCategory_IsNoOp()
    {
        using var tmp = new TempArtccs();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "ZOA", "CustomFixes"));

        var result = CustomProcedureLoader.LoadAll(tmp.Path);

        Assert.Empty(result.Fragments);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LoadAll_ExtractsAirportIcaos_AndIgnoresHeaderAndShortLines()
    {
        using var tmp = new TempArtccs();
        tmp.WriteFragment(
            "ZOA",
            "bay-area.cifp",
            [
                "# KOAK NIMITZ FIVE — provenance header, ignored by the parser",
                "",
                "SUSAP KSFO too short to be a record",
                Record("KOAK", 'D', "NIMI5"),
                Record("KOAK", 'D', "NIMI5"),
                Record("KSJC", 'E', "JAKKE4"),
            ]
        );

        var result = CustomProcedureLoader.LoadAll(tmp.Path);

        var fragment = Assert.Single(result.Fragments);
        Assert.Equal("ZOA", fragment.ArtccId);
        Assert.Equal(["KOAK", "KSJC"], fragment.AirportIcaos.OrderBy(i => i, StringComparer.Ordinal));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LoadAll_FragmentWithNoRecords_WarnsAndSkips()
    {
        using var tmp = new TempArtccs();
        tmp.WriteFragment("ZOA", "empty.cifp", ["# nothing but a header", "SUSAP KOAK but far too short"]);

        var result = CustomProcedureLoader.LoadAll(tmp.Path);

        Assert.Empty(result.Fragments);
        Assert.Single(result.Warnings);
        Assert.Contains("no CIFP airport records", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAll_LowercaseArtccDirectory_NormalizesToUppercase()
    {
        using var tmp = new TempArtccs();
        tmp.WriteFragment("zoa", "koak.cifp", [Record("KOAK", 'D', "NIMI5")]);

        var result = CustomProcedureLoader.LoadAll(tmp.Path);

        Assert.Equal("ZOA", Assert.Single(result.Fragments).ArtccId);
    }

    [Fact]
    public void LoadAll_MultipleArtccs_AreReturnedInSortedOrder()
    {
        using var tmp = new TempArtccs();
        tmp.WriteFragment("ZOA", "koak.cifp", [Record("KOAK", 'D', "NIMI5")]);
        tmp.WriteFragment("ZLA", "klax.cifp", [Record("KLAX", 'D', "ORCKA3")]);

        var result = CustomProcedureLoader.LoadAll(tmp.Path);

        // Sorted enumeration is what makes a duplicate-procedure conflict resolve deterministically.
        Assert.Equal(["ZLA", "ZOA"], result.Fragments.Select(f => f.ArtccId));
    }

    [Fact]
    public void LoadAll_IgnoresNonCifpExtensions()
    {
        using var tmp = new TempArtccs();
        string dir = Path.Combine(tmp.Path, "ZOA", "Procedures");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "notes.txt"), [Record("KOAK", 'D', "NIMI5")]);
        File.WriteAllLines(Path.Combine(dir, "koak.json"), [Record("KOAK", 'D', "NIMI5")]);

        var result = CustomProcedureLoader.LoadAll(tmp.Path);

        Assert.Empty(result.Fragments);
        Assert.Empty(result.Warnings);
    }

    private sealed class TempArtccs : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("yaat-custom-proc-").FullName;

        public void WriteFragment(string artcc, string fileName, IEnumerable<string> lines)
        {
            string dir = System.IO.Path.Combine(Path, artcc, "Procedures");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(System.IO.Path.Combine(dir, fileName), lines);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

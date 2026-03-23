using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for custom fix loading and DCT resolution.
/// Reproduces issue #99: DCT OAK30NUM fails on server because custom fixes
/// aren't resolved by CommandParser.ParseCompound.
/// </summary>
[Collection("NavDbMutator")]
public class CustomFixTests
{
    public CustomFixTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // --- Test 1: DCT with custom fix when fix is already in NavDb (parser test) ---

    [Fact]
    public void DirectTo_CustomFix_ParsesWhenFixInNavDb()
    {
        var navDb = NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["OAK30NUM"] = (37.702, -122.215) });
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var result = CommandParser.ParseCompound("DCT OAK30NUM", null);

        Assert.True(result.IsSuccess, $"ParseCompound failed: {result.Reason}");
        var compound = result.Value!;
        Assert.Single(compound.Blocks);
        var cmd = compound.Blocks[0].Commands[0];
        Assert.IsType<DirectToCommand>(cmd);

        var dct = (DirectToCommand)cmd;
        Assert.Single(dct.Fixes);
        Assert.Equal("OAK30NUM", dct.Fixes[0].Name);
        Assert.Empty(dct.SkippedFixes);
    }

    // --- Test 2: Custom fix loading from actual files ---

    [Fact]
    public void CustomFixLoader_LoadsOak30NumFromDataDir()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "Data", "custom_fixes");

        var loadResult = CustomFixLoader.LoadAll(baseDir);

        // Should find at least OAK30NUM
        Assert.Empty(loadResult.Warnings);
        Assert.NotEmpty(loadResult.Fixes);

        var oak30Def = loadResult.Fixes.FirstOrDefault(f => f.Aliases.Contains("OAK30NUM", StringComparer.OrdinalIgnoreCase));
        Assert.NotNull(oak30Def);
        Assert.NotNull(oak30Def!.Lat);
        Assert.NotNull(oak30Def.Lon);
    }

    // --- Test 3: NavigationDatabase with default custom fix path resolves OAK30NUM ---

    [Fact]
    public void NavigationDatabase_DefaultPath_LoadsCustomFixes()
    {
        var navDbPath = Path.Combine("TestData", "NavData.dat");
        if (!File.Exists(navDbPath))
        {
            return; // Skip if no test data
        }

        var bytes = File.ReadAllBytes(navDbPath);
        var navData = Yaat.Sim.Proto.NavDataSet.Parser.ParseFrom(bytes);

        // Resolve CIFP same way TestVnasData does
        var cifpGz = Path.Combine("TestData", "FAACIFP18.gz");
        if (!File.Exists(cifpGz))
        {
            return; // Skip if no CIFP
        }

        var cifpPath = DecompressGzip(cifpGz);

        // Use default customFixesBaseDir (null) — forces AppContext.BaseDirectory path
        var db = new NavigationDatabase(navData, cifpPath);

        var pos = db.GetFixPosition("OAK30NUM");
        Assert.NotNull(pos);
        Assert.InRange(pos!.Value.Lat, 37.70, 37.71);
        Assert.InRange(pos.Value.Lon, -122.22, -122.21);

        // Also check VP915 (second fix in same file)
        var vp915 = db.GetFixPosition("VP915");
        Assert.NotNull(vp915);

        // Verify it appears in AllFixNames for autocomplete
        Assert.Contains(db.AllFixNames, n => n.Equals("OAK30NUM", StringComparison.OrdinalIgnoreCase));
    }

    // --- Test 4: Full round-trip ParseCompound with custom fix loaded (mirrors server) ---

    [Fact]
    public void ParseCompound_DirectToCustomFix_WithRealNavDb()
    {
        var navDbPath = Path.Combine("TestData", "NavData.dat");
        if (!File.Exists(navDbPath))
        {
            return;
        }

        var bytes = File.ReadAllBytes(navDbPath);
        var navData = Yaat.Sim.Proto.NavDataSet.Parser.ParseFrom(bytes);

        var cifpGz = Path.Combine("TestData", "FAACIFP18.gz");
        if (!File.Exists(cifpGz))
        {
            return;
        }

        var cifpPath = DecompressGzip(cifpGz);

        // Initialize with default custom fix path (same as server does)
        var db = new NavigationDatabase(navData, cifpPath);
        using var _ = NavigationDatabase.ScopedOverride(db);

        // This is exactly what the server does in RoomEngine.SendCommand
        var result = CommandParser.ParseCompound("DCT OAK30NUM", null);

        Assert.True(result.IsSuccess, $"ParseCompound failed: {result.Reason}");
        var dct = (DirectToCommand)result.Value!.Blocks[0].Commands[0];
        Assert.Single(dct.Fixes);
        Assert.Equal("OAK30NUM", dct.Fixes[0].Name);
    }

    private static string DecompressGzip(string gzPath)
    {
        var decompressedPath = Path.Combine(Path.GetTempPath(), "FAACIFP18-customfix-test");
        using var inputStream = File.OpenRead(gzPath);
        using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
        using var outputStream = File.Create(decompressedPath);
        gzipStream.CopyTo(outputStream);
        return decompressedPath;
    }
}

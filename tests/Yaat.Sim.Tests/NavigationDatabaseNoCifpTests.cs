using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// A <see cref="NavigationDatabase.ForTesting" /> instance carries no CIFP file. Asking it for
/// procedures must come back empty rather than throwing out of the parser.
///
/// This bites across test classes, not just within one: <c>NavigationDatabase</c> is a static singleton,
/// and any test that installs a CIFP-less instance via <c>SetInstance</c> leaves it there for every later
/// test in the process. A later test that resolves procedures for an aircraft's destination then died on
/// <c>File.ReadLines("")</c> — far from the test that actually installed the instance.
/// <c>LoadAirportMagneticVariation</c> already guarded this way; the procedure loaders did not.
/// </summary>
public class NavigationDatabaseNoCifpTests
{
    private static NavigationDatabase CifpLessDb() =>
        NavigationDatabase.ForTesting(
            new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase) { ["OAK"] = (37.7213, -122.2208) }
        );

    [Fact]
    public void GetSids_WithNoCifpFile_ReturnsEmpty()
    {
        Assert.Empty(CifpLessDb().GetSids("KOAK"));
    }

    [Fact]
    public void GetStars_WithNoCifpFile_ReturnsEmpty()
    {
        Assert.Empty(CifpLessDb().GetStars("KOAK"));
    }

    [Fact]
    public void GetApproaches_WithNoCifpFile_ReturnsEmpty()
    {
        Assert.Empty(CifpLessDb().GetApproaches("KOAK"));
    }

    /// <summary>The magnetic-variation loader already guarded — pin it so the four stay consistent.</summary>
    [Fact]
    public void GetAirportMagneticVariation_WithNoCifpFile_ReturnsNull()
    {
        Assert.Null(CifpLessDb().GetAirportMagneticVariation("KOAK"));
    }

    /// <summary>
    /// The path that actually failed: resolving procedure patterns for a destination airport, which is
    /// what <c>MainViewModel.BuildSpeechContext</c> does for every aircraft in the room.
    /// </summary>
    [Fact]
    public void GetProcedurePatterns_WithNoCifpFile_ReturnsEmpty()
    {
        Assert.Empty(CifpLessDb().GetProcedurePatterns(["KOAK", "KSFO"]));
    }
}

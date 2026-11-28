using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// The DME-radial predicate behind FRD anchoring: a navaid that gives both a magnetic radial and a DME — a VOR/DME, a
/// VORTAC or a TACAN — can anchor a fix-radial-distance; a plain VOR, a standalone DME, an NDB/DME and an ILS/DME or
/// LOC/DME cannot. The spoken type collapses a VOR/DME to "VOR", so the ARINC 424 class field carries the distinction.
/// </summary>
public class NavigationDatabaseDmeRadialTests
{
    public NavigationDatabaseDmeRadialTests() => TestVnasData.EnsureInitialized();

    private static NavigationDatabase NavDb => NavigationDatabase.Instance;

    [Theory]
    [InlineData("VD   ")] // VOR/DME
    [InlineData("VT   ")] // VORTAC
    [InlineData(" T   ")] // TACAN, whose record the parser drops today
    [InlineData(" M   ")] // TACAN, same
    public void ClassFieldWithARadialAndADme_HasDmeRadial(string navClass) => Assert.True(CifpParser.HasDmeRadial(false, navClass.AsSpan()));

    [Theory]
    [InlineData("V    ")] // plain VOR: no ranging facility
    [InlineData(" D   ")] // standalone DME: no radial
    [InlineData(" I   ")] // ILS/DME or LOC/DME: a localizer gives no radials
    public void ClassFieldWithoutARadialOrADme_HasNoDmeRadial(string navClass) => Assert.False(CifpParser.HasDmeRadial(false, navClass.AsSpan()));

    [Fact]
    public void NdbRecord_HasNoDmeRadial() => Assert.False(CifpParser.HasDmeRadial(true, "VTTW ".AsSpan()));

    [Fact]
    public void RealNavaids_VorDmeAndVortacAreDmeRadial_PlainVorIsNot()
    {
        Assert.True(NavDb.IsDmeRadialNavaid("OAK")); // OAKLAND VOR/DME
        Assert.True(NavDb.IsDmeRadialNavaid("SFO")); // SAN FRANCISCO VOR/DME
        Assert.True(NavDb.IsDmeRadialNavaid("CCR")); // CONCORD VOR/DME
        Assert.True(NavDb.IsDmeRadialNavaid("SGD")); // SAUSALITO VORTAC
        Assert.False(NavDb.IsDmeRadialNavaid("VCV")); // VICTORVILLE VOR, no DME
        Assert.False(NavDb.IsDmeRadialNavaid("BUD")); // BUDD VOR, no DME
    }

    [Fact]
    public void StandaloneDmeNavaid_IsNotLoadedAndNotDmeRadial()
    {
        Assert.Null(NavDb.GetNavaidType("WVI")); // WATSONVILLE DME only
        Assert.False(NavDb.IsDmeRadialNavaid("WVI"));
    }

    [Fact]
    public void ParsedNavaidRecords_CarryTheSameFlagAsTheDatabase()
    {
        IReadOnlyDictionary<string, CifpNavaid> navaids = CifpParser.ParseNavaids(TestVnasData.GetCifpPath()!);

        string[] idents = ["OAK", "SFO", "CCR", "SGD", "VCV", "BUD"];
        foreach (string ident in idents)
        {
            Assert.Equal(NavDb.IsDmeRadialNavaid(ident), navaids[ident].HasDmeRadial);
        }
    }
}

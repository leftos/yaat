using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="StarsPositionSymbol"/> mirrors CRC's <c>TrackManager.UpdateOwnerPositionSymbol</c>: the character a STARS
/// scope draws for a track's owner. On the real ZOA config, FAT's handoff list is NCT=1, BFL=2, NLC=3, and NCT's lists
/// FAT as 3. <see cref="StarsPositionSymbol.ForeignOwnerSymbol"/> is the suffix YAAT appends to the owner's TCP
/// (<c>3Y(1)</c>) when the student's STARS scope draws an owner in another facility.
/// </summary>
public class StarsPositionSymbolTests
{
    private static readonly TrackOwner FatStudent = TrackOwner.CreateStars("FAT_1F_APP", "FAT", 1, "F");
    private static readonly TrackOwner NctStudent = TrackOwner.CreateStars("NCT_1M_APP", "NCT", 1, "M");
    private static readonly TrackOwner Nct3Y = TrackOwner.CreateStars("MOD_APP", "NCT", 3, "Y");

    [Fact]
    public void ForeignOwnerSymbol_OwnerInStudentsFacility_IsNull()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var owner = TrackOwner.CreateStars("FAT_1F_APP", "FAT", 1, "F");

        Assert.Equal('F', StarsPositionSymbol.ForViewer(owner, FatStudent, zoa));
        Assert.Null(StarsPositionSymbol.ForeignOwnerSymbol(owner, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_Nct3YForFatStudent_Is1()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        Assert.Equal("1", StarsPositionSymbol.ForeignOwnerSymbol(Nct3Y, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_FatOwnerForNctStudent_Is3()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var owner = TrackOwner.CreateStars("FAT_1F_APP", "FAT", 1, "F");

        Assert.Equal("3", StarsPositionSymbol.ForeignOwnerSymbol(owner, NctStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_FacilityMissingFromHandoffList_IsC()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var owner = TrackOwner.CreateStars("SUU_1N_APP", "SUU", 1, "N");

        Assert.Equal("C", StarsPositionSymbol.ForeignOwnerSymbol(owner, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_ZoaCentreOwner_IsC()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var owner = TrackOwner.CreateEram("OAK_44_CTR", "ZOA", "44");

        Assert.Equal("C", StarsPositionSymbol.ForeignOwnerSymbol(owner, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_ZlaCentreOwner_IsL()
    {
        ArtccConfigRoot? snapshot = TestArtccConfig.LoadZoa();
        ArtccConfigRoot? zla = TestArtccConfig.LoadZla();
        if ((snapshot is null) || (zla is null))
        {
            return;
        }

        // A copy: the shared ZOA snapshot is not mutated. The server fills the neighbour letters at load.
        ArtccConfigRoot zoa = JsonSerializer.Deserialize<ArtccConfigRoot>(JsonSerializer.Serialize(snapshot))!;
        zoa.NeighborCenterNasIds["ZLA"] = zla.Facility.EramConfiguration!.NasId!;
        var owner = TrackOwner.CreateEram("LAX_25_CTR", "ZLA", "25");

        Assert.Equal("L", StarsPositionSymbol.ForeignOwnerSymbol(owner, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_ZobNeighbour_OwnCentreOwnerShowsNasId()
    {
        ArtccConfigRoot? snapshot = TestArtccConfig.LoadZoa();
        if (snapshot is null)
        {
            return;
        }

        // A copy: the shared ZOA snapshot is not mutated. CRC shows the own centre's NAS ID to a facility neighbouring ZOB.
        ArtccConfigRoot zoa = JsonSerializer.Deserialize<ArtccConfigRoot>(JsonSerializer.Serialize(snapshot))!;
        zoa.FindFacility("FAT")!.NeighboringFacilityIds.Add("ZOB");
        var owner = TrackOwner.CreateEram("OAK_44_CTR", "ZOA", "44");

        Assert.Equal("O", StarsPositionSymbol.ForeignOwnerSymbol(owner, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_NoConfig_IsNull() => Assert.Null(StarsPositionSymbol.ForeignOwnerSymbol(Nct3Y, FatStudent, null));

    [Fact]
    public void ForeignOwnerSymbol_CentreWithoutNasId_IsC()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var owner = TrackOwner.CreateEram("SEA_12_CTR", "ZSE", "12");

        Assert.Equal("C", StarsPositionSymbol.ForeignOwnerSymbol(owner, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_Unowned_IsNull()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        Assert.Null(StarsPositionSymbol.ForViewer(null, FatStudent, zoa));
        Assert.Null(StarsPositionSymbol.ForeignOwnerSymbol(null, FatStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_NoStudent_IsNull()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        Assert.Null(StarsPositionSymbol.ForeignOwnerSymbol(Nct3Y, null, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_EramStudent_IsNull()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var eramStudent = TrackOwner.CreateEram("OAK_44_CTR", "ZOA", "44");

        Assert.Null(StarsPositionSymbol.ForeignOwnerSymbol(Nct3Y, eramStudent, zoa));
    }

    [Fact]
    public void ForeignOwnerSymbol_NonNasOwner_IsNull()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        Assert.Equal('C', StarsPositionSymbol.ForViewer(TrackOwner.Elsewhere, FatStudent, zoa));
        Assert.Null(StarsPositionSymbol.ForeignOwnerSymbol(TrackOwner.Elsewhere, FatStudent, zoa));
    }
}

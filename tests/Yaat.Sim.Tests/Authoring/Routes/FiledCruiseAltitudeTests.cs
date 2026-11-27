using Xunit;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="FiledCruiseAltitude"/> files the preferred route's published altitude when it gives one, else the IFR
/// direction-of-flight altitude nearest the type's usual cruise (capped on a short trip, floored 3,000 ft above the
/// higher field), on the committed navdata's airport positions and field elevations and the real aircraft tables.
/// </summary>
public class FiledCruiseAltitudeTests
{
    private static readonly DateTime ModelDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public FiledCruiseAltitudeTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void KOAKtoKLAX_B738_Eastbound_FilesFL350() => Assert.Equal(35000, Pick("B738", "KOAK", "KLAX", null));

    [Fact]
    public void KSFOtoKSEA_B738_Westbound_FilesFL340() => Assert.Equal(34000, Pick("B738", "KSFO", "KSEA", null));

    [Fact]
    public void KOAKtoKRNO_B738_ShortTripCap_FilesFL230() => Assert.Equal(23000, Pick("B738", "KOAK", "KRNO", null));

    [Fact]
    public void KSFOtoKJFK_B738_Eastbound_FilesFL350() => Assert.Equal(35000, Pick("B738", "KSFO", "KJFK", null));

    [Fact]
    public void KSFOtoKSEA_E145_Westbound_FilesFL320() => Assert.Equal(32000, Pick("E145", "KSFO", "KSEA", null));

    [Fact]
    public void KSFOtoKJFK_GLF5_Eastbound_AboveFL410_FilesFL450() => Assert.Equal(45000, Pick("GLF5", "KSFO", "KJFK", null));

    [Fact]
    public void KSFOtoKSEA_GLF5_Westbound_AboveFL410_FilesFL430() => Assert.Equal(43000, Pick("GLF5", "KSFO", "KSEA", null));

    [Fact]
    public void KSFOtoKJFK_PC12_Eastbound_Tie_PrefersLower() => Assert.Equal(23000, Pick("PC12", "KSFO", "KJFK", null));

    [Fact]
    public void KSFOtoKSMF_B738_TecClassCodeJ30_FilesPublished3000() => Assert.Equal(3000, Pick("B738", "KSFO", "KSMF", "J30"));

    // Floor = max(field elevations) + 3,000 ft; the short-trip cap leaves the usual cruise well below the floor, so the
    // nearest eastbound altitude at or above it is 5,000.
    [Fact]
    public void KSFOtoKOAK_B738_FloorLiftsTheFiledAltitudeToFL050() => Assert.Equal(5000, Pick("B738", "KSFO", "KOAK", null));

    // Floor = KCOS's field elevation + 3,000 ft, which lands in the 9,001-11,000 ft band; the cap (150 ft/NM) leaves the
    // usual cruise below the floor, so the nearest eastbound altitude above it is 11,000.
    [Fact]
    public void KDENtoKCOS_B738_FloorLiftsTheFiledAltitudeToFL110() => Assert.Equal(11000, Pick("B738", "KDEN", "KCOS", null));

    [Fact]
    public void KSFOtoKOAK_B738_Published4000_IgnoresTheFloor() => Assert.Equal(4000, Pick("B738", "KSFO", "KOAK", "4000"));

    [Fact]
    public void KOAKtoKOAK_B738_SameAirport_LeavesTheFieldEmpty() =>
        Assert.Null(FiledCruiseAltitude.Pick("B738", Airport("KOAK"), Airport("KOAK"), preferredAltitude: null, ModelDate));

    [Fact]
    public void ZZZZ_UnknownType_LeavesTheFieldEmpty() =>
        Assert.Null(FiledCruiseAltitude.Pick("ZZZZ", Airport("KSFO"), Airport("KLAX"), preferredAltitude: null, ModelDate));

    [Fact]
    public void R44_Helicopter_NoUsualCruise_LeavesTheFieldEmpty() =>
        Assert.Null(FiledCruiseAltitude.Pick("R44", Airport("KSFO"), Airport("KOAK"), preferredAltitude: null, ModelDate));

    [Fact]
    public void KSFOtoKSEA_PC12_AOB1000_Westbound_FilesTheBandHighBound() => Assert.Equal(1000, Pick("PC12", "KSFO", "KSEA", "AOB 1000"));

    // PC12 ceiling 30,000 ft: a band wholly above it, a list wholly above it and a single above it all leave no
    // preferred altitude, so the tie of 23,000 and 25,000 eastbound takes 23,000.
    [Fact]
    public void KSFOtoKJFK_PC12_BandAboveCeiling_FilesTheDirectionAltitude() => Assert.Equal(23000, Pick("PC12", "KSFO", "KJFK", "FL310-FL350"));

    [Fact]
    public void KSFOtoKJFK_PC12_ListAboveCeiling_FilesTheDirectionAltitude() => Assert.Equal(23000, Pick("PC12", "KSFO", "KJFK", "FL390 / FL410"));

    [Fact]
    public void KSFOtoKJFK_PC12_SingleAboveCeiling_FilesTheDirectionAltitude() => Assert.Equal(23000, Pick("PC12", "KSFO", "KJFK", "FL390"));

    // The list is matched against the capped usual cruise (150 ft/NM, below both listed values), and both 23,000 and
    // 35,000 are eastbound levels, so 23,000 is the nearer to the cap.
    [Fact]
    public void KOAKtoKRNO_B738_ListPicksAgainstTheCappedUsual_FilesFL230() => Assert.Equal(23000, Pick("B738", "KOAK", "KRNO", "FL230 / FL350"));

    [Fact]
    public void KSFOtoKJFK_B738_FL240WithSpace_FilesPublished24000() => Assert.Equal(24000, Pick("B738", "KSFO", "KJFK", "FL 240"));

    [Fact]
    public void KOAKtoKRNO_B738_Published35000_IgnoresTheCap() => Assert.Equal(35000, Pick("B738", "KOAK", "KRNO", "FL350"));

    [Fact]
    public void KOAKtoKRNO_B738_ClassCodeJ50_FilesPublished5000() => Assert.Equal(5000, Pick("B738", "KOAK", "KRNO", "J50"));

    [Fact]
    public void KSFOtoKSEA_PC12_ClassCodeM50_FilesPublished5000() => Assert.Equal(5000, Pick("PC12", "KSFO", "KSEA", "M50"));

    [Theory]
    [InlineData("B738", 9000)]
    [InlineData("PC12", 9000)]
    public void KSFOtoKJFK_ClassCodeJM90_FilesTheClassLetterAltitude(string aircraftType, int expectedFt) =>
        Assert.Equal(expectedFt, Pick(aircraftType, "KSFO", "KJFK", "JM90"));

    [Fact]
    public void KOAKtoKLAX_B738_ClassCodeWithNoJetLetter_FilesTheDirectionAltitude() => Assert.Equal(35000, Pick("B738", "KOAK", "KLAX", "PQ50"));

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(179.4, true)]
    [InlineData(179.6, false)]
    [InlineData(180.0, false)]
    [InlineData(359.6, true)]
    public void IsEastboundMagneticCourse_RoundsToTheNearestWholeDegree(double courseMagnetic, bool expected) =>
        Assert.Equal(expected, FiledCruiseAltitude.IsEastboundMagneticCourse(courseMagnetic));

    [Theory]
    [InlineData("KSFO", "KJFK", "FL240 AND ABOVE", 35000)]
    [InlineData("KSFO", "KJFK", "AOB FL220", 21000)]
    [InlineData("KSFO", "KJFK", "70-170 INCL", 17000)]
    [InlineData("KSFO", "KJFK", "080-100", 9000)]
    [InlineData("KSFO", "KJFK", "6000 / 8000", 8000)]
    [InlineData("KSFO", "KJFK", "BLO 110", 9000)]
    [InlineData("KSFO", "KJFK", "WATER", 35000)]
    [InlineData("KSFO", "KSEA", "70-170 INCL", 16000)]
    [InlineData("KSFO", "KSEA", "080-100", 10000)]
    public void B738_PreferredAltitude_FilesTheDirectionAltitudeNearestTheUsualCruise(
        string departure,
        string destination,
        string preferredAltitude,
        int expectedFt
    ) => Assert.Equal(expectedFt, Pick("B738", departure, destination, preferredAltitude));

    private static int Pick(string aircraftType, string departure, string destination, string? preferredAltitude)
    {
        int? filed = FiledCruiseAltitude.Pick(aircraftType, Airport(departure), Airport(destination), preferredAltitude, ModelDate);
        Assert.NotNull(filed);
        return filed.Value;
    }

    private static CruiseAirport Airport(string id)
    {
        (double Lat, double Lon) position =
            NavigationDatabase.Instance.GetAirportPosition(id) ?? throw new InvalidOperationException($"{id} is not in the committed navdata");
        double elevation =
            NavigationDatabase.Instance.GetAirportElevation(id)
            ?? throw new InvalidOperationException($"{id} has no field elevation in the committed navdata");
        return new CruiseAirport(new LatLon(position.Lat, position.Lon), elevation);
    }
}

using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The vTDLS editor header composes the type and equipment cell itself: the server sends the bare ICAO designator and
/// the suffix in separate fields, and this is where they are joined. An aircraft filed without an equipment suffix
/// shows the bare type — never a dangling separator.
/// </summary>
public class TdlsFlightPlanInfoDtoTests
{
    private static TdlsFlightPlanInfoDto Info(string aircraftType, string equipmentSuffix) =>
        new(
            AssignedBeaconCode: 1234,
            Departure: "KOAK",
            Destination: "KLAX",
            Route: "SUNOL ALTAM",
            AircraftType: aircraftType,
            EquipmentSuffix: equipmentSuffix,
            Remarks: "",
            Cid: "",
            CruiseAltitude: 35000
        );

    [Fact]
    public void TypeAndEquipment_JoinsTheTypeAndTheSuffix()
    {
        Assert.Equal("B77W/L", Info("B77W", "L").TypeAndEquipment);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TypeAndEquipment_WithoutASuffix_IsTheBareType(string? equipmentSuffix)
    {
        Assert.Equal("B77W", Info("B77W", equipmentSuffix!).TypeAndEquipment);
    }
}

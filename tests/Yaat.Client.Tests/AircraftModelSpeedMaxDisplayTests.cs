using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.Tests;

/// <summary>
/// While SMAX ("maintain maximum forward speed") is in force the aircraft list's assigned-speed column shows <c>MAX</c> with
/// the live figure (<c>MAX 330</c> below the IAS/Mach crossover, <c>MAX M.80</c> above it), and the EuroScope tag shows
/// <c>SMAX</c>.
/// </summary>
public class AircraftModelSpeedMaxDisplayTests
{
    [Fact]
    public void BelowTheCrossover_ShowsMaxAndTheKnots()
    {
        var ac = new AircraftModel { SpeedMax = true, AssignedSpeed = 330 };

        Assert.Equal("MAX 330", ac.AssignedSpeedDisplay);
    }

    [Fact]
    public void AboveTheCrossover_ShowsMaxAndTheMach()
    {
        var ac = new AircraftModel { SpeedMax = true, AssignedMach = 0.80 };

        Assert.Equal("MAX M.80", ac.AssignedSpeedDisplay);
    }

    [Fact]
    public void WithoutSmax_ShowsTheFigureAlone()
    {
        var ac = new AircraftModel { AssignedSpeed = 250 };

        Assert.Equal("250", ac.AssignedSpeedDisplay);
        Assert.Equal("S250", EuroScopeTagLayout.FormatAssignedSpeed(ac));
    }

    [Fact]
    public void EuroScopeTag_ShowsSmax()
    {
        var ac = new AircraftModel { SpeedMax = true, AssignedSpeed = 330 };

        Assert.Equal("SMAX", EuroScopeTagLayout.FormatAssignedSpeed(ac));
    }

    [Fact]
    public void FromDto_And_UpdateFromDto_CarrySpeedMax()
    {
        var ac = AircraftModel.FromDto(Dto(speedMax: true, assignedSpeed: 330));
        Assert.True(ac.SpeedMax);
        Assert.Equal("MAX 330", ac.AssignedSpeedDisplay);

        ac.UpdateFromDto(Dto(speedMax: false, assignedSpeed: 250));
        Assert.False(ac.SpeedMax);
        Assert.Equal("250", ac.AssignedSpeedDisplay);
    }

    private static AircraftDto Dto(bool speedMax, double? assignedSpeed) =>
        new(
            Callsign: "AAL123",
            AircraftType: "B738",
            Latitude: 37.5,
            Longitude: -122.0,
            Heading: 90,
            Altitude: 8000,
            GroundSpeed: 340,
            BeaconCode: 4571,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: assignedSpeed,
            Departure: "KSFO",
            Destination: "KJFK",
            Route: "DCT",
            FlightRules: "IFR",
            Status: "",
            SpeedMax: speedMax
        );
}

using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// A filed Mach number or classified speed leaves the knots field at 0, so the client writes the filed speed the way ERAM
/// does (<c>M078</c>, <c>SC</c>) in the flight-plan summary and cruise display instead of showing nothing.
/// </summary>
public class AircraftModelCruiseSpeedDisplayTests
{
    [Fact]
    public void MachPlan_ShowsMachInTheSummaryAndCruiseDisplay()
    {
        var ac = new AircraftModel { CruiseAltitude = 35000, CruiseMach = 78 };

        Assert.Equal("M078", ac.FiledSpeedDisplay);
        Assert.Equal("FL350 / M078", ac.CruiseDisplay);
        Assert.Contains("FL350/M078", ac.FlightPlanDisplay);
    }

    [Fact]
    public void ClassifiedPlan_ShowsScInTheSummaryAndCruiseDisplay()
    {
        var ac = new AircraftModel { CruiseAltitude = 35000, IsSpeedClassified = true };

        Assert.Equal("SC", ac.FiledSpeedDisplay);
        Assert.Equal("FL350 / SC", ac.CruiseDisplay);
        Assert.Contains("FL350/SC", ac.FlightPlanDisplay);
    }

    [Fact]
    public void KnotsPlan_KeepsTheKnotsSuffix()
    {
        var ac = new AircraftModel { CruiseAltitude = 35000, CruiseSpeed = 450 };

        Assert.Equal("450", ac.FiledSpeedDisplay);
        Assert.Equal("FL350 / 450 kt", ac.CruiseDisplay);
        Assert.Contains("FL350/450kt", ac.FlightPlanDisplay);
    }

    [Fact]
    public void NoSpeed_ShowsTheAltitudeAlone()
    {
        var ac = new AircraftModel { CruiseAltitude = 35000 };

        Assert.Equal("", ac.FiledSpeedDisplay);
        Assert.Equal("FL350", ac.CruiseDisplay);
        Assert.DoesNotContain("FL350/", ac.FlightPlanDisplay);
    }

    [Fact]
    public void EditorSpeed_IsEmptyWithTheMachAsPlaceholder_ForAMachPlan()
    {
        var ac = new AircraftModel { CruiseAltitude = 35000, CruiseMach = 78 };

        Assert.Equal("", ac.EditorSpeedText);
        Assert.Equal("M078", ac.EditorSpeedPlaceholder);
    }

    [Fact]
    public void EditorSpeed_IsTheKnotsWithNoPlaceholder_ForAKnotsPlan()
    {
        var ac = new AircraftModel { CruiseAltitude = 35000, CruiseSpeed = 450 };

        Assert.Equal("450", ac.EditorSpeedText);
        Assert.Null(ac.EditorSpeedPlaceholder);
    }

    [Fact]
    public void FromDto_And_UpdateFromDto_CarryMachAndClassifiedSpeed()
    {
        var ac = AircraftModel.FromDto(Dto(cruiseMach: 78, isSpeedClassified: false));
        Assert.Equal(78, ac.CruiseMach);
        Assert.False(ac.IsSpeedClassified);

        ac.UpdateFromDto(Dto(cruiseMach: null, isSpeedClassified: true));
        Assert.Null(ac.CruiseMach);
        Assert.True(ac.IsSpeedClassified);
        Assert.Equal("FL350 / SC", ac.CruiseDisplay);
    }

    private static AircraftDto Dto(int? cruiseMach, bool isSpeedClassified) =>
        new(
            Callsign: "AAL123",
            AircraftType: "B738",
            Latitude: 37.5,
            Longitude: -122.0,
            Heading: 90,
            Altitude: 35000,
            GroundSpeed: 450,
            BeaconCode: 4571,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "KSFO",
            Destination: "KJFK",
            Route: "DCT",
            FlightRules: "IFR",
            Status: "",
            CruiseAltitude: 35000,
            CruiseSpeed: 0,
            CruiseMach: cruiseMach,
            IsSpeedClassified: isSpeedClassified
        );
}

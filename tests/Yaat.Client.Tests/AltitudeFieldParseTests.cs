using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for AircraftModel.ParseAltitudeField — verifies flight rules inference
/// from the altitude text field in the flight plan editor.
/// </summary>
public class AltitudeFieldParseTests
{
    [Fact]
    public void EmptyText_ReturnsVfr()
    {
        var result = AircraftModel.ParseAltitudeField("");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(0, result.Value.CruiseAltitude);
    }

    [Fact]
    public void WhitespaceText_ReturnsVfr()
    {
        var result = AircraftModel.ParseAltitudeField("  ");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(0, result.Value.CruiseAltitude);
    }

    [Fact]
    public void BareNumber_ReturnsIfr()
    {
        var result = AircraftModel.ParseAltitudeField("120");
        Assert.NotNull(result);
        Assert.Equal("IFR", result.Value.FlightRules);
        Assert.Equal(12000, result.Value.CruiseAltitude);
    }

    [Fact]
    public void VfrKeyword_ReturnsVfr()
    {
        var result = AircraftModel.ParseAltitudeField("VFR");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(0, result.Value.CruiseAltitude);
    }

    [Fact]
    public void VfrWithAltitude_ReturnsVfr()
    {
        var result = AircraftModel.ParseAltitudeField("VFR/055");
        Assert.NotNull(result);
        Assert.Equal("VFR", result.Value.FlightRules);
        Assert.Equal(5500, result.Value.CruiseAltitude);
    }
}

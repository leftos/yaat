using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The Airplane Design Group helpers the precompute cache keys its push targets on: the roman parse of an FAA record's
/// ADG and the smallest group whose span ceiling covers a wingspan.
/// </summary>
public class AirplaneDesignGroupsTests
{
    [Theory]
    [InlineData("I", AirplaneDesignGroup.I)]
    [InlineData("II", AirplaneDesignGroup.II)]
    [InlineData("iii", AirplaneDesignGroup.III)]
    [InlineData("Iv", AirplaneDesignGroup.IV)]
    [InlineData("V", AirplaneDesignGroup.V)]
    [InlineData("vi", AirplaneDesignGroup.VI)]
    public void TryParseRoman_I_To_VI_AndGarbage(string text, AirplaneDesignGroup expected)
    {
        Assert.True(AirplaneDesignGroups.TryParseRoman(text, out AirplaneDesignGroup group));
        Assert.Equal(expected, group);

        foreach (string? garbage in (string?[])[null, "", " ", "VII", "0", "4", "IIII", " II", "II ", "X"])
        {
            Assert.False(AirplaneDesignGroups.TryParseRoman(garbage, out _), $"'{garbage}' parsed as a design group");
        }
    }

    [Theory]
    [InlineData(10.0, AirplaneDesignGroup.I)]
    [InlineData(48.9, AirplaneDesignGroup.I)]
    [InlineData(49.0, AirplaneDesignGroup.II)]
    [InlineData(49.1, AirplaneDesignGroup.II)]
    [InlineData(79.0, AirplaneDesignGroup.III)]
    [InlineData(118.0, AirplaneDesignGroup.IV)]
    [InlineData(171.0, AirplaneDesignGroup.V)]
    [InlineData(214.0, AirplaneDesignGroup.VI)]
    [InlineData(262.0, AirplaneDesignGroup.VI)]
    [InlineData(262.1, AirplaneDesignGroup.VI)]
    public void SmallestCoveringSpan_Limits(double spanFt, AirplaneDesignGroup expected) =>
        Assert.Equal(expected, AirplaneDesignGroups.SmallestCoveringSpan(spanFt));

    [Fact]
    public void OfRecord_NoAdgAndNoSpan_IsNull()
    {
        var record = new FaaAircraftRecord { IcaoCode = "NONE", Adg = "" };

        Assert.Null(AirplaneDesignGroups.OfRecord(record, out string? warning));
        Assert.Equal("NONE: ADG is blank and the record has no wingspan; it has no design group", warning);
    }

    /// <summary>AC 150/5300-13B Table 1-2 bounds each group's span from above exclusively: group I is under 49 ft.</summary>
    [Fact]
    public void OfRecord_BlankAdgAnd49FtSpan_IsGroupII()
    {
        var record = new FaaAircraftRecord
        {
            IcaoCode = "EDGE",
            Adg = "",
            WingspanFtWithoutWinglets = 49.0,
        };

        Assert.Equal(AirplaneDesignGroup.II, AirplaneDesignGroups.OfRecord(record, out string? warning));
        Assert.Null(warning);
    }

    [Fact]
    public void OfRecord_NoAdgAndSpanOver262_IsGroupVI_WithAWarning()
    {
        var record = new FaaAircraftRecord
        {
            IcaoCode = "HUGE",
            Adg = "",
            WingspanFtWithoutWinglets = 290.0,
        };

        Assert.Equal(AirplaneDesignGroup.VI, AirplaneDesignGroups.OfRecord(record, out string? warning));
        Assert.Equal("HUGE has no ADG and its 290 ft span exceeds group VI's ceiling; using group VI", warning);
    }
}

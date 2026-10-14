using Xunit;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Tests;

/// <summary>
/// Builds a <see cref="GriddedWindField"/> from the real NOMADS GFS subset described on <see cref="Grib2ReaderTests"/>
/// (cycle 2026-10-14 12Z, f000, ZOA box). Expected U/V are the ecCodes-decoded values quoted there.
/// </summary>
public class GriddedWindFieldTests
{
    private const double MsToKt = 1.0 / 0.514444;

    private static GriddedWindField BuildSample() => GriddedWindField.FromMessages(Grib2Reader.Read(Grib2ReaderTests.LoadSample()));

    [Fact]
    public void FromMessages_Sample_ExposesCycleBoundsAndLevels()
    {
        GriddedWindField field = BuildSample();

        Assert.Equal(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc), field.ReferenceTimeUtc);
        Assert.Equal(0, field.ForecastHours);
        Assert.Equal(34.0, field.SouthLatDeg, 9);
        Assert.Equal(42.0, field.NorthLatDeg, 9);
        Assert.Equal(234.0, field.WestLonDeg, 9);
        Assert.Equal(243.0, field.EastLonDeg, 9);
        Assert.Equal(20, field.LevelsHpa.Count);
        Assert.Equal(1000.0, field.LevelsHpa[0], 9);
        Assert.Equal(150.0, field.LevelsHpa[^1], 9);
    }

    [Fact]
    public void SampleColumn_ExactGridPoint_EqualsReferenceWindConverted()
    {
        // Grid point (j 16, i 18) = 38.0 N, 238.5 E; ecCodes U/V at 500 hPa.
        IReadOnlyList<GriddedWindSample> column = BuildSample().SampleColumn(38.0, -121.5)!;

        AssertWind(At500(column), 6.574102783203125, -14.463365173339845);
    }

    [Fact]
    public void SampleColumn_CellCentre_IsMeanOfFourCornersUAndV()
    {
        // Cell between grid points (j 0..1, i 0..1): 34.125 N, 234.125 E; ecCodes U/V at its corners at 500 hPa.
        double u = (6.2741027832031255 + 6.374102783203125 + 6.574102783203125 + 6.474102783203126) / 4.0;
        double v = (-8.663365173339844 + -9.263365173339844 + -8.563365173339845 + -8.963365173339843) / 4.0;

        IReadOnlyList<GriddedWindSample> column = BuildSample().SampleColumn(34.125, -125.875)!;

        AssertWind(At500(column), u, v);
    }

    [Theory]
    [InlineData(42.5, -120.0)]
    [InlineData(33.9, -120.0)]
    [InlineData(38.0, -126.5)]
    [InlineData(38.0, -116.5)]
    public void SampleColumn_OutsideGrid_ReturnsNull(double latDeg, double lonDeg) => Assert.Null(BuildSample().SampleColumn(latDeg, lonDeg));

    [Fact]
    public void SampleColumn_NegativeAndPositiveLongitude_GiveSameColumn()
    {
        GriddedWindField field = BuildSample();

        IReadOnlyList<GriddedWindSample> west = field.SampleColumn(37.7, -122.2)!;
        IReadOnlyList<GriddedWindSample> east = field.SampleColumn(37.7, 237.8)!;

        Assert.Equal(west.Count, east.Count);
        for (int k = 0; k < west.Count; k++)
        {
            Assert.Equal(west[k].DirectionTrueDeg, east[k].DirectionTrueDeg, 9);
            Assert.Equal(west[k].SpeedKts, east[k].SpeedKts, 9);
            Assert.Equal(west[k].TemperatureC, east[k].TemperatureC, 9);
        }
    }

    [Fact]
    public void SampleColumn_IsOrderedByAltitudeWithTwentyLevels()
    {
        IReadOnlyList<GriddedWindSample> column = BuildSample().SampleColumn(37.7, -122.2)!;

        Assert.Equal(20, column.Count);
        for (int k = 1; k < column.Count; k++)
        {
            Assert.True(column[k].AltitudeFt > column[k - 1].AltitudeFt, $"level {k} is not above level {k - 1}");
        }

        Assert.Equal(18_289, At500(column).AltitudeFt, 50.0);
    }

    [Fact]
    public void SampleColumn_ExactGridPoint_TemperatureIsReferenceInCelsius()
    {
        // Grid point (j 16, i 18) TMP at 500 hPa from ecCodes, in kelvin.
        IReadOnlyList<GriddedWindSample> column = BuildSample().SampleColumn(38.0, -121.5)!;

        Assert.Equal(264.221015625 - 273.15, At500(column).TemperatureC, 1e-3);
    }

    [Fact]
    public void FromMessages_UMissingAtOneLevel_ThrowsNamingTheLevel()
    {
        List<Grib2Message> messages = [.. Grib2Reader.Read(Grib2ReaderTests.LoadSample())];
        messages.RemoveAll(m => (m.ParameterCategory == 2) && (m.ParameterNumber == 2) && (m.FirstSurfaceValue == 50000.0));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => GriddedWindField.FromMessages(messages));

        Assert.Contains("500 hPa", ex.Message);
    }

    [Fact]
    public void SampleColumn_OffCentrePoint_WeightsCornersByFraction()
    {
        // 34.1875 N, 234.0625 E lies Fx = 0.25 east and Fy = 0.75 north of grid point (j 0, i 0); ecCodes U/V at 500 hPa.
        const double fx = 0.25;
        const double fy = 0.75;
        (double U, double V) j0i0 = (6.2741027832031255, -8.663365173339844);
        (double U, double V) j0i1 = (6.374102783203125, -9.263365173339844);
        (double U, double V) j1i0 = (6.574102783203125, -8.563365173339845);
        (double U, double V) j1i1 = (6.474102783203126, -8.963365173339843);
        double u = ((1 - fy) * (((1 - fx) * j0i0.U) + (fx * j0i1.U))) + (fy * (((1 - fx) * j1i0.U) + (fx * j1i1.U)));
        double v = ((1 - fy) * (((1 - fx) * j0i0.V) + (fx * j0i1.V))) + (fy * (((1 - fx) * j1i0.V) + (fx * j1i1.V)));

        IReadOnlyList<GriddedWindSample> column = BuildSample().SampleColumn(34.1875, -125.9375)!;

        AssertWind(At500(column), u, v);
    }

    [Fact]
    public void SampleColumn_ExactGridPoint_MatchesHandWorkedDirectionAndSpeed()
    {
        // (j 16, i 18) at 500 hPa: U = 6.5741, V = −14.4634 m/s blows toward the south-south-east.
        // Speed √(6.5741² + 14.4634²) = 15.887 m/s = 30.88 kt; FROM = 360° − atan(6.5741 / 14.4634) = 360° − 24.44° = 335.56° true.
        GriddedWindSample sample = At500(BuildSample().SampleColumn(38.0, -121.5)!);

        Assert.Equal(30.88, sample.SpeedKts, 0.01);
        Assert.Equal(335.56, sample.DirectionTrueDeg, 0.01);
    }

    [Fact]
    public void SampleColumn_NorthEastCorner_EqualsCornerGridValue()
    {
        // 42.0 N, 243.0 E is grid point (j 32, i 36): the cell clamps to i0 = Ni − 2 with Fx = Fy = 1.
        List<Grib2Message> messages = SampleMessages();
        int corner = (32 * 37) + 36;
        double u = messages[IndexOf(messages, 2, 2, 50000)].Values[corner];
        double v = messages[IndexOf(messages, 2, 3, 50000)].Values[corner];

        IReadOnlyList<GriddedWindSample> column = GriddedWindField.FromMessages(messages).SampleColumn(42.0, -117.0)!;

        AssertWind(At500(column), u, v);
    }

    [Fact]
    public void SampleColumn_NaNCorner_DropsThatLevelOnly()
    {
        List<Grib2Message> messages = SampleMessages();
        int u500 = IndexOf(messages, 2, 2, 50000);
        float[] values = [.. messages[u500].Values];
        values[0] = float.NaN; // grid point (j 0, i 0)
        messages[u500] = Copy(messages[u500], values, messages[u500].FirstSurfaceValue);
        var field = GriddedWindField.FromMessages(messages);

        IReadOnlyList<GriddedWindSample> touching = field.SampleColumn(34.125, -125.875)!;
        IReadOnlyList<GriddedWindSample> away = field.SampleColumn(38.0, -121.5)!;

        Assert.Equal(19, touching.Count);
        Assert.DoesNotContain(touching, s => s.PressureHpa == 500.0);
        Assert.Equal(20, away.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SampleColumn_WindFromDueNorth_DirectionIsPositiveZero(bool negativeZeroU)
    {
        float uMs = negativeZeroU ? -0.0f : 0.0f;
        List<Grib2Message> messages = SampleMessages();
        int u500 = IndexOf(messages, 2, 2, 50000);
        int v500 = IndexOf(messages, 2, 3, 50000);
        messages[u500] = Copy(messages[u500], [.. Enumerable.Repeat(uMs, messages[u500].Values.Length)], 50000);
        messages[v500] = Copy(messages[v500], [.. Enumerable.Repeat(-5f, messages[v500].Values.Length)], 50000);

        GriddedWindSample sample = At500(GriddedWindField.FromMessages(messages).SampleColumn(38.0, -121.5)!);

        Assert.Equal(0.0, sample.DirectionTrueDeg);
        Assert.False(double.IsNegative(sample.DirectionTrueDeg), "direction is −0");
    }

    [Fact]
    public void FromMessages_ShortValueArray_ThrowsNamingParameterAndLevel()
    {
        List<Grib2Message> messages = SampleMessages();
        int u500 = IndexOf(messages, 2, 2, 50000);
        messages[u500] = Copy(messages[u500], messages[u500].Values[..^1], 50000);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => GriddedWindField.FromMessages(messages));

        Assert.Contains("UGRD at 500 hPa", ex.Message);
    }

    [Fact]
    public void FromMessages_LevelAtZeroPascals_Throws()
    {
        List<Grib2Message> messages = SampleMessages();
        int u500 = IndexOf(messages, 2, 2, 50000);
        messages[u500] = Copy(messages[u500], messages[u500].Values, 0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => GriddedWindField.FromMessages(messages));

        Assert.Contains("UGRD at 0 hPa", ex.Message);
    }

    [Fact]
    public void FromMessages_LevelRoundingToZeroPascals_Throws()
    {
        List<Grib2Message> messages = SampleMessages();
        int u500 = IndexOf(messages, 2, 2, 50000);
        int v500 = IndexOf(messages, 2, 3, 50000);
        int t500 = IndexOf(messages, 0, 0, 50000);
        messages[u500] = Copy(messages[u500], messages[u500].Values, 0.4);
        messages[v500] = Copy(messages[v500], messages[v500].Values, 0.4);
        messages[t500] = Copy(messages[t500], messages[t500].Values, 0.4);

        Assert.Throws<InvalidOperationException>(() => GriddedWindField.FromMessages(messages));
    }

    private static List<Grib2Message> SampleMessages() => [.. Grib2Reader.Read(Grib2ReaderTests.LoadSample())];

    private static int IndexOf(List<Grib2Message> messages, int category, int number, double pa) =>
        messages.FindIndex(m => (m.ParameterCategory == category) && (m.ParameterNumber == number) && (m.FirstSurfaceValue == pa));

    private static Grib2Message Copy(Grib2Message source, float[] values, double surfaceValue) =>
        new()
        {
            Discipline = source.Discipline,
            ParameterCategory = source.ParameterCategory,
            ParameterNumber = source.ParameterNumber,
            FirstSurfaceType = source.FirstSurfaceType,
            FirstSurfaceValue = surfaceValue,
            ReferenceTimeUtc = source.ReferenceTimeUtc,
            ForecastHours = source.ForecastHours,
            Grid = source.Grid,
            Values = values,
        };

    private static GriddedWindSample At500(IReadOnlyList<GriddedWindSample> column) => Assert.Single(column, s => s.PressureHpa == 500.0);

    private static void AssertWind(GriddedWindSample sample, double uMs, double vMs)
    {
        double expectedSpeed = Math.Sqrt((uMs * uMs) + (vMs * vMs)) * MsToKt;
        double expectedFrom = ((Math.Atan2(-uMs, -vMs) * 180.0 / Math.PI) + 360.0) % 360.0;

        Assert.Equal(expectedSpeed, sample.SpeedKts, 1e-3);
        Assert.Equal(expectedFrom, sample.DirectionTrueDeg, 1e-3);
    }
}

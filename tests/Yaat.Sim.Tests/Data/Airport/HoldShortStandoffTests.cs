using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The base hold-short separation S of a runway: a paint calibration first, then the authored <c>holdShortDistance</c>,
/// then the rule from the runway's width, length, ILS category and elevation (AC 150/5300-13B Table 3-2), with the rule
/// overriding an authored value more than 15 ft below it on a runway in the 250 ft class.
/// </summary>
public class HoldShortStandoffTests(ITestOutputHelper output)
{
    private const double LongFt = 6_000.0;

    [Theory]
    [InlineData(200.0, LongFt, null, 280.0)] // W >= 200
    [InlineData(199.0, LongFt, null, 250.0)]
    [InlineData(150.0, LongFt, 2, 280.0)] // W >= 150 and CAT II/III
    [InlineData(150.0, LongFt, 3, 280.0)]
    [InlineData(149.0, LongFt, 2, 250.0)]
    [InlineData(150.0, LongFt, 1, 250.0)]
    [InlineData(120.0, 5_000.0, null, 250.0)] // W >= 100 and L >= 5,000
    [InlineData(120.0, 4_999.0, null, 200.0)] // W >= 100
    [InlineData(120.0, 4_999.0, 1, 250.0)] // W >= 100 and any ILS
    [InlineData(100.0, LongFt, null, 250.0)]
    [InlineData(99.0, LongFt, null, 150.0)] // W >= 75
    [InlineData(99.0, LongFt, 1, 175.0)]
    [InlineData(75.0, LongFt, null, 150.0)]
    [InlineData(74.0, LongFt, null, 125.0)] // else
    [InlineData(74.0, LongFt, 1, 175.0)]
    public void Rule_ByWidthLengthAndIls(double widthFt, double lengthFt, int? ilsCategory, double expectedFt)
    {
        var inputs = new RunwayStandoffInputs(widthFt, lengthFt, ilsCategory, 0.0);
        HoldShortStandoffResult result = HoldShortStandoff.BaseSeparationFt(null, null, inputs);

        Assert.Equal(expectedFt, result.Ft, precision: 6);
        Assert.Equal(HoldShortStandoffSource.Rule, result.Source);
    }

    /// <summary>The rows of 250 ft and more add a foot per 100 ft of elevation; the rows below do not.</summary>
    [Theory]
    [InlineData(150.0, 1_000.0, 260.0)]
    [InlineData(200.0, 640.0, 286.4)]
    [InlineData(75.0, 1_000.0, 150.0)]
    public void Rule_AddsTheElevationTermFrom250Up(double widthFt, double elevationFt, double expectedFt) =>
        Assert.Equal(
            expectedFt,
            HoldShortStandoff.BaseSeparationFt(null, null, new RunwayStandoffInputs(widthFt, LongFt, null, elevationFt)).Ft,
            precision: 6
        );

    [Fact]
    public void Calibrated_BeatsAuthored()
    {
        HoldShortStandoffResult result = HoldShortStandoff.BaseSeparationFt(252.0, 225.0, new RunwayStandoffInputs(150.0, LongFt, 1, 0.0));

        Assert.Equal(252.0, result.Ft, precision: 6);
        Assert.Equal(HoldShortStandoffSource.Calibrated, result.Source);
    }

    [Fact]
    public void Authored_AtRuleMinus15_Wins()
    {
        HoldShortStandoffResult result = HoldShortStandoff.BaseSeparationFt(null, 235.0, new RunwayStandoffInputs(150.0, LongFt, null, 0.0));

        Assert.Equal(235.0, result.Ft, precision: 6);
        Assert.Equal(HoldShortStandoffSource.Authored, result.Source);
    }

    [Fact]
    public void Authored_MoreThan15BelowA250ClassRule_LosesToTheRule_WithAWarning()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 100);
        SimLogBuilder.CreateForTest(output).CaptureInto(tap).InitializeSimLog();

        HoldShortStandoffResult result = HoldShortStandoff.BaseSeparationFt(null, 234.9, new RunwayStandoffInputs(150.0, LongFt, null, 0.0));
        HoldShortStandoff.WarnWhenRuleOverAuthored("OAK", "28R - 10L", 234.9, result);

        Assert.Equal(250.0, result.Ft, precision: 6);
        Assert.Equal(HoldShortStandoffSource.RuleOverAuthored, result.Source);
        CapturedLogRecord warning = Assert.Single(tap.Drain(), record => record.Category == "HoldShortStandoff");
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("OAK", warning.Message, StringComparison.Ordinal);
        Assert.Contains("28R - 10L", warning.Message, StringComparison.Ordinal);
        Assert.Contains("234.9", warning.Message, StringComparison.Ordinal);
        Assert.Contains("250", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>Below the 250 ft class the authored value stands however far below the rule it is, and logs nothing.</summary>
    [Fact]
    public void Authored_BelowThe250Class_IsKeptEvenFarBelowTheRule()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 100);
        SimLogBuilder.CreateForTest(output).CaptureInto(tap).InitializeSimLog();

        HoldShortStandoffResult result = HoldShortStandoff.BaseSeparationFt(null, 100.0, new RunwayStandoffInputs(120.0, 4_999.0, null, 0.0));
        HoldShortStandoff.WarnWhenRuleOverAuthored("TEST", "1 - 19", 100.0, result);

        Assert.Equal(100.0, result.Ft, precision: 6);
        Assert.Equal(HoldShortStandoffSource.Authored, result.Source);
        Assert.DoesNotContain(tap.Drain(), record => record.Category == "HoldShortStandoff");
    }

    /// <summary>The taxiway width proxy steps down with the crossed runway's width: 75, 50, 35, then 25 ft.</summary>
    [Theory]
    [InlineData(150.0, 75.0)]
    [InlineData(149.9, 50.0)]
    [InlineData(100.0, 50.0)]
    [InlineData(99.9, 35.0)]
    [InlineData(75.0, 35.0)]
    [InlineData(74.9, 25.0)]
    public void TaxiwayWidthProxy_ByRunwayWidth(double runwayWidthFt, double expectedFt) =>
        Assert.Equal(expectedFt, HoldShortStandoff.TaxiwayWidthProxyFt(runwayWidthFt));

    /// <summary>
    /// The centerline standoff adds half the proxy taxiway width times |cos θ|: nothing for a perpendicular taxiway, the
    /// whole half-width for one along the runway either way.
    /// </summary>
    [Theory]
    [InlineData(250.0, 150.0, 90.0, 250.0)]
    [InlineData(250.0, 150.0, 0.0, 287.5)]
    [InlineData(250.0, 150.0, 180.0, 287.5)]
    [InlineData(250.0, 150.0, 45.0, 250.0 + (37.5 * 0.70710678118654752))]
    [InlineData(280.0, 200.0, 60.0, 298.75)]
    public void CenterlineStandoff_AddsTheAngleTerm(double baseFt, double runwayWidthFt, double angleDeg, double expectedFt) =>
        Assert.Equal(expectedFt, HoldShortStandoff.CenterlineStandoffFt(baseFt, runwayWidthFt, angleDeg), 1e-9);
}

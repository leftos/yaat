using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins <see cref="TrackPresenter.EramAltitudeText"/>, the ERAM FDB line-2 Field B + B4 + C text, against
/// <c>docs/crc/eram.md</c> Table 3 and CRC's <c>BaseDataBlockRenderObject</c> (<c>GetFieldB</c>, <c>GetFieldB4</c>,
/// <c>GetVerticalConformance</c>, <c>GetFieldC</c>, <c>FormatAltitude</c>). Where the two disagree CRC wins, as the
/// student's display draws it. Expectations write the climb and descend marks as <c>↑</c>/<c>↓</c>; the formatter
/// returns <see cref="TrackPresenter.EramArrowCell"/> in that cell with an arrow style.
/// </summary>
public class TrackPresenterEramAltitudeTests
{
    /// <summary>A track at FL253 with Mode C reported, no plan altitude and nothing else set.</summary>
    private static EramAltitudeInput At(int? reported) => new() { ReportedAltitude = reported, ModeCPreviouslyReceived = true };

    public static TheoryData<string, EramAltitudeInput, string> Table3Rows =>
        new()
        {
            // C: reached and maintaining FL300; field C hidden.
            { "C", At(300) with { PlanAltitudeFeet = 30000, ReachedAssignedAltitude = true }, "300C" },
            { "T", At(253) with { PlanAltitudeFeet = 30000, InterimAltitude = 100 }, "100T253" },
            { "L", At(253) with { PlanAltitudeFeet = 30000, LocalInterimAltitude = 100 }, "100L253" },
            { "P", At(253) with { PlanAltitudeFeet = 30000, ProcedureAltitude = 100 }, "100P253" },
            { "↑", At(253) with { PlanAltitudeFeet = 30000 }, "300↑253" },
            { "↓", At(253) with { PlanAltitudeFeet = 23000 }, "230↓253" },
            { "+", At(253) with { PlanAltitudeFeet = 23000, ReachedAssignedAltitude = true }, "230+253" },
            { "-", At(212) with { PlanAltitudeFeet = 23000, ReachedAssignedAltitude = true }, "230-212" },
            { "X", At(null) with { PlanAltitudeFeet = 23000 }, "230XXXX" },
            { "A", At(null) with { PlanAltitudeFeet = 23000, ControllerEnteredAltitude = 230 }, "230A" },
            // CRC differs: Table 3 shows 230#210, but GetFieldB4 never returns '#'; the CERA is out of conformance, so B4 is
            // the conformance mark (no ±2 tolerance for a CERA) and '#' is appended to field C (FormatAltitude isCera).
            { "#", At(null) with { PlanAltitudeFeet = 23000, ControllerEnteredAltitude = 210 }, "230↑210#" },
            // CRC differs: Table 3 shows VFR/055, but GetFieldC formats a VFR track's reported altitude unpadded.
            { "/ VFR", At(55) with { PlanAltitudeFeet = 5500, IsVfr = true }, "VFR/55" },
            { "/ OTP", At(218) with { PlanAltitudeFeet = 21000, IsVfrOnTop = true }, "OTP/218" },
            // CRC differs: Table 3 shows 200B353, but above the block the conformance mark replaces B (GetFieldB4's
            // switch) and field B stays the floor (GetFieldB).
            { "B above", At(353) with { PlanAltitudeFeet = 25000, BlockFloorFeet = 20000 }, "200↓353" },
            // B within the block: field C shows the block ceiling.
            { "B within", At(230) with { PlanAltitudeFeet = 25000, BlockFloorFeet = 20000 }, "200B250" },
            // CRC differs: Table 3 shows 200B193, but below the block field B is the ceiling (GetFieldB, Low/Climbing)
            // and B4 the conformance mark.
            { "B below", At(193) with { PlanAltitudeFeet = 25000, BlockFloorFeet = 20000 }, "250↑193" },
            // N: no target correlated (Mode C never received); field C empty for a non-block altitude.
            { "N", At(null) with { PlanAltitudeFeet = 23000, ModeCPreviouslyReceived = false }, "230N" },
        };

    public static TheoryData<string, EramAltitudeInput, string> ConformanceAndCrcRows =>
        new()
        {
            { "within the +-2 tolerance is in conformance", At(298) with { PlanAltitudeFeet = 30000 }, "300C" },
            { "just below the tolerance climbs", At(297) with { PlanAltitudeFeet = 30000 }, "300↑297" },
            { "ABV has no ceiling", At(350) with { PlanAltitudeFeet = 30000, IsAbove = true }, "300C" },
            {
                "CERA high after reaching",
                At(null) with
                {
                    PlanAltitudeFeet = 23000,
                    ControllerEnteredAltitude = 250,
                    ReachedAssignedAltitude = true,
                },
                "230+250#"
            },
            {
                "CERA within a block shows the ceiling with #",
                At(null) with
                {
                    PlanAltitudeFeet = 25000,
                    BlockFloorFeet = 20000,
                    ControllerEnteredAltitude = 220,
                },
                "200B250#"
            },
            { "CERA with an interim", At(253) with { PlanAltitudeFeet = 30000, InterimAltitude = 100, ControllerEnteredAltitude = 240 }, "100T240#" },
            { "no plan altitude", At(253), "V" },
            { "Mode C lost with an interim", At(null) with { PlanAltitudeFeet = 30000, InterimAltitude = 100 }, "100TXXX" },
            {
                "no target with an interim",
                At(null) with
                {
                    PlanAltitudeFeet = 30000,
                    InterimAltitude = 100,
                    ModeCPreviouslyReceived = false,
                },
                "100T"
            },
            {
                "no target with a block shows the ceiling",
                At(null) with
                {
                    PlanAltitudeFeet = 25000,
                    BlockFloorFeet = 20000,
                    ModeCPreviouslyReceived = false,
                },
                "200N250"
            },
            {
                "local interim wins over procedure and interim",
                At(253) with
                {
                    PlanAltitudeFeet = 30000,
                    LocalInterimAltitude = 120,
                    ProcedureAltitude = 110,
                    InterimAltitude = 100,
                },
                "120L253"
            },
            { "procedure wins over interim", At(253) with { PlanAltitudeFeet = 30000, ProcedureAltitude = 110, InterimAltitude = 100 }, "110P253" },
            { "interim below FL100 is unpadded", At(45) with { PlanAltitudeFeet = 30000, InterimAltitude = 50 }, "50T45" },
            { "OTP without an altitude", At(95) with { IsVfrOnTop = true }, "OTP/95" },
            // GetFieldC's VFR arm formats the reported altitude without isCera, so a VFR CERA takes no '#'.
            { "VFR with a CERA takes no #", At(null) with { PlanAltitudeFeet = 5500, IsVfr = true, ControllerEnteredAltitude = 60 }, "VFR/60" },
        };

    /// <summary>
    /// CRC's GetReportedAltitude: a coasting track (QT coast or lost coverage) reports no Mode C altitude, so the line takes
    /// the no-report path (<c>X</c>/<c>XXX</c> after Mode C was received); a controller-entered altitude still reports.
    /// </summary>
    [Theory]
    [InlineData(true, false, null, null, "230XXXX")]
    [InlineData(false, true, null, null, "230XXXX")]
    [InlineData(true, false, 150, null, "150TXXX")]
    [InlineData(false, true, 150, null, "150TXXX")]
    [InlineData(true, false, null, 230, "230A")]
    public void CoastingTrack_ReportsNoModeC(bool coastTrack, bool coverageCoasting, int? interim, int? cera, string expected)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramPlanAltitudeFeet = 23000;
        ac.EramReachedAssignedAltitude = true;
        ac.TransponderHasReportedModeC = true;
        ac.EramIsCoastTrack = coastTrack;
        ac.EramIsCoverageCoasting = coverageCoasting;
        ac.EramInterimAltitude = interim;
        ac.EramControllerEnteredAltitude = cera;

        var input = EramAltitudeInput.From(ac);

        Assert.Null(input.ReportedAltitude);
        AssertAltitude("coasting", input, expected);
    }

    [Fact]
    public void NotCoasting_InConformance_ReportsModeC()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramPlanAltitudeFeet = 23000;
        ac.EramReachedAssignedAltitude = true;

        Assert.Equal(230, EramAltitudeInput.From(ac).ReportedAltitude);
        AssertAltitude("not coasting", EramAltitudeInput.From(ac), "230C");
    }

    [Theory]
    [MemberData(nameof(Table3Rows))]
    public void Table3Row_MatchesCrc(string row, EramAltitudeInput input, string expected) => AssertAltitude(row, input, expected);

    [Theory]
    [MemberData(nameof(ConformanceAndCrcRows))]
    public void ConformanceAndCrcRow_MatchesCrc(string row, EramAltitudeInput input, string expected) => AssertAltitude(row, input, expected);

    [Fact]
    public void ArrowCell_IsTheFourthCharacterAndDrawsNoGlyph()
    {
        EramAltitude climbing = TrackPresenter.EramAltitudeText(At(253) with { PlanAltitudeFeet = 30000 });

        Assert.Equal(3, climbing.Text.IndexOf(TrackPresenter.EramArrowCell, StringComparison.Ordinal));
        Assert.DoesNotContain('↑', climbing.Text);
        Assert.Equal(ScopeFieldStyle.ArrowUp, climbing.Style);
    }

    [Fact]
    public void NonArrowStates_CarryNoArrowStyle()
    {
        Assert.Equal(ScopeFieldStyle.None, TrackPresenter.EramAltitudeText(At(230) with { PlanAltitudeFeet = 23000 }).Style);
        Assert.Equal(
            ScopeFieldStyle.None,
            TrackPresenter.EramAltitudeText(At(253) with { PlanAltitudeFeet = 23000, ReachedAssignedAltitude = true }).Style
        );
    }

    private static void AssertAltitude(string row, EramAltitudeInput input, string expected)
    {
        ScopeFieldStyle expectedStyle =
            expected.Contains('↑') ? ScopeFieldStyle.ArrowUp
            : expected.Contains('↓') ? ScopeFieldStyle.ArrowDown
            : ScopeFieldStyle.None;
        string expectedText = expected.Replace("↑", TrackPresenter.EramArrowCell).Replace("↓", TrackPresenter.EramArrowCell);

        EramAltitude actual = TrackPresenter.EramAltitudeText(input);

        Assert.True(expectedText == actual.Text, $"row {row}: expected '{expectedText}', got '{actual.Text}'");
        Assert.Equal(expectedStyle, actual.Style);
    }
}

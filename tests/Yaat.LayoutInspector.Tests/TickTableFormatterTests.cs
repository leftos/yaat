using Xunit;
using Yaat.LayoutInspector.Tick;

namespace Yaat.LayoutInspector.Tests;

/// <summary>
/// The tick table's speed-cap columns: <c>brake</c> carries the navigator's braking-curve ceiling and <c>arc</c> the
/// current fillet arc's cornering cap, both printed as <c>-</c> when the tick computed none
/// (<see cref="double.MaxValue"/>), the same "unset" the HTML renderer writes by omitting the field.
/// </summary>
public class TickTableFormatterTests
{
    [Fact]
    public void PrintTable_HeaderCarriesAnArcColumnAfterBrake()
    {
        string text = Render([Row(20.0, 12.5)]);

        string header = Lines(text)[0];
        string[] expected = ["tick", "hdg", "gs", "nav", "distFt", "brg", "angd", "tgtSpd", "brake", "arc", "onArc"];
        Assert.Equal(expected, Tokens(header));
    }

    [Fact]
    public void PrintTable_ArcColumnShowsTheCapAndBlanksAnUnsetOne()
    {
        string text = Render([Row(20.0, 12.5), Row(20.0, double.MaxValue), Row(double.MaxValue, double.MaxValue)]);

        // lines[0] is the header and lines[1] the rule under it; the data rows follow.
        string[] lines = Lines(text);
        Assert.Equal("12.5", Tokens(lines[2])[9]);
        Assert.Equal("-", Tokens(lines[3])[9]);
        Assert.Equal("-", Tokens(lines[4])[8]);
        Assert.Equal("-", Tokens(lines[4])[9]);
    }

    private static string Render(List<TickDataRow> rows)
    {
        var writer = new StringWriter { NewLine = "\n" };
        TickTableFormatter.PrintTable(writer, rows, refLine: null, exitRefs: []);
        return writer.ToString();
    }

    private static TickDataRow Row(double brakeLimit, double arcLimit) =>
        new(
            Time: 1,
            Callsign: "TEST",
            Lat: 37.7,
            Lon: -122.2,
            Hdg: 90.0,
            Gs: 10.0,
            Phase: "Taxiing",
            Twy: "B",
            NavTarget: 5,
            NavDist: 0.1,
            NavBrg: 90.0,
            NavAngleDiff: 0.0,
            NavTargetSpd: 15.0,
            NavBrakeLimit: brakeLimit,
            NavArcLimit: arcLimit,
            NavOnArc: true,
            NavNodeReqSpd: 10.0,
            Status: ""
        );

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Tokens(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

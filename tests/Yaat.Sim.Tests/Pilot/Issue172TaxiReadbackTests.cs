using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Issue #172 W7 follow-up: the pilot now voices the taxi route in the readback (previously TAXI
/// produced no spoken readback), including the controller's <c>&gt;</c>/<c>&lt;</c> turn as
/// "right turn / left turn onto &lt;taxiway&gt;". Driven through the rule-inversion verbalizer, so a
/// path-only command reads "taxi via …" and richer clearances pick up runway / hold-short / cross.
/// </summary>
public class Issue172TaxiReadbackTests
{
    public Issue172TaxiReadbackTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Taxi_PathOnly_VoicesRoute()
    {
        var result = PhraseologyVerbalizer.Verbalize(new TaxiCommand(["B", "C"], []));
        Assert.Equal("taxi via bravo, charlie", result);
    }

    [Fact]
    public void Taxi_WithTurnHints_VoicesTurns()
    {
        var taxi = new TaxiCommand(["B", "C"], [], PathTurnHints: [TurnDirection.Right, null]);
        var result = PhraseologyVerbalizer.Verbalize(taxi);
        Assert.Equal("taxi via right on bravo, charlie", result);
    }

    [Fact]
    public void Taxi_LeftTurnMidRoute_VoicesLeftTurn()
    {
        var taxi = new TaxiCommand(["A", "B", "C"], [], PathTurnHints: [null, null, TurnDirection.Left]);
        var result = PhraseologyVerbalizer.Verbalize(taxi);
        Assert.Equal("taxi via alpha, bravo, left on charlie", result);
    }

    [Fact]
    public void Taxi_WithHoldShort_VoicesHoldShort()
    {
        var result = PhraseologyVerbalizer.Verbalize(new TaxiCommand(["B", "C"], ["28R"]));
        Assert.Equal("taxi via bravo, charlie hold short of runway two eight right", result);
    }

    [Fact]
    public void Taxi_NodeRefOnly_ProducesNoReadback()
    {
        // A draw-route debug taxi (node refs only) has no spoken form.
        var result = PhraseologyVerbalizer.Verbalize(new TaxiCommand(["#42", "#18"], []));
        Assert.Null(result);
    }
}

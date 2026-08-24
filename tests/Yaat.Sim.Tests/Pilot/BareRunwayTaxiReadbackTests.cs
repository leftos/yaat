using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A TAXI with no route named — the bare <c>TAXI 1L</c> an aircraft already at its runway gets (issue #393),
/// and <c>TAXIAUTO 1L</c> — reads back "taxi to runway one left". Before this the path-less form had no
/// readback at all (<c>TaxiArgs</c> bailed on an empty path), and a fuller clearance must keep winning the
/// richer via rule.
/// </summary>
public class BareRunwayTaxiReadbackTests(ITestOutputHelper output)
{
    [Fact]
    public void Verbalize_PathlessTaxiToRunway_SpokenAndTerminal()
    {
        TestVnasData.EnsureInitialized();
        var taxi = new TaxiCommand([], [], "1L");

        var spoken = PhraseologyVerbalizer.Verbalize(taxi);
        var terminal = PhraseologyVerbalizer.VerbalizeTerminal(taxi);
        output.WriteLine($"spoken:   {spoken}");
        output.WriteLine($"terminal: {terminal}");

        Assert.Equal("taxi to runway one left", spoken);
        Assert.Equal("taxi to runway 1L", terminal);
    }

    [Fact]
    public void Verbalize_TaxiAuto_ReadsBackLikePathlessTaxi()
    {
        TestVnasData.EnsureInitialized();
        var spoken = PhraseologyVerbalizer.Verbalize(new TaxiAutoCommand("28R"));
        output.WriteLine($"spoken: {spoken}");

        Assert.Equal("taxi to runway two eight right", spoken);
    }

    [Fact]
    public void Verbalize_TaxiWithRouteAndRunway_StillVoicesTheRoute()
    {
        TestVnasData.EnsureInitialized();
        var spoken = PhraseologyVerbalizer.Verbalize(new TaxiCommand(["A", "B"], [], "28R"));
        output.WriteLine($"spoken: {spoken}");

        Assert.Contains("runway two eight right", spoken);
        Assert.Contains("via alpha, bravo", spoken);
    }

    [Fact]
    public void Verbalize_NodeRefOnlyTaxi_StaysSilent()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(PhraseologyVerbalizer.Verbalize(new TaxiCommand(["#42", "#18"], [])));
    }
}

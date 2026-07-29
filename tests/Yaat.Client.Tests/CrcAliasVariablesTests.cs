using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class CrcAliasVariablesTests
{
    private static readonly CrcAliasContext Oakland = new("KOAK", "KJFK", "SUNOL Q126 ALTAM");

    [Fact]
    public void DepAndArr_ResolveFromTheFlightPlan()
    {
        Assert.Equal("dep=KOAK&dest=KJFK", CrcAliasVariables.Substitute("dep=$dep&dest=$arr", Oakland));
    }

    [Fact]
    public void FullRoute_IsDepartureRouteDestination()
    {
        Assert.Equal("KOAK SUNOL Q126 ALTAM KJFK", CrcAliasVariables.Substitute("$fullroute", Oakland));
    }

    /// <summary>vNAS routes can carry a leading <c>+</c> marker, which CRC strips for display.</summary>
    [Fact]
    public void Route_StripsASingleLeadingPlus()
    {
        var context = new CrcAliasContext("KOAK", "KJFK", "+SUNOL Q126");

        Assert.Equal("SUNOL Q126", CrcAliasVariables.Substitute("$route", context));
        Assert.Equal("KOAK SUNOL Q126 KJFK", CrcAliasVariables.Substitute("$fullroute", context));
    }

    [Fact]
    public void NoFlightPlan_ResolvesToTheCrcSentinel()
    {
        var text = CrcAliasVariables.Substitute("$dep $arr $route $fullroute", CrcAliasContext.None);

        Assert.Equal("---- ---- ---- ----", text);
    }

    [Fact]
    public void BlankField_ResolvesToTheCrcSentinel()
    {
        Assert.Equal("----", CrcAliasVariables.Substitute("$arr", new CrcAliasContext("KOAK", "", "SUNOL")));
    }

    /// <summary>
    /// The two-pass ordering is what makes this work: <c>$fullroute</c> is already literal by the time
    /// the function pass runs. Without it <c>.openurl</c> would truncate the URL at the first space.
    /// </summary>
    [Fact]
    public void UrlEscape_EncodesAnAlreadySubstitutedVariable()
    {
        var text = CrcAliasVariables.Substitute("https://skyvector.com/?fpl=$urlescape($fullroute)", Oakland);

        Assert.Equal("https://skyvector.com/?fpl=KOAK%20SUNOL%20Q126%20ALTAM%20KJFK", text);
    }

    [Fact]
    public void UnknownVariables_AreLeftLiteral()
    {
        Assert.Equal("$squawk $freq()", CrcAliasVariables.Substitute("$squawk $freq()", Oakland));
    }

    /// <summary>Positional slots are already resolved by the store, so this pass must not touch them.</summary>
    [Fact]
    public void PositionalSlots_AreLeftLiteral()
    {
        Assert.Equal("$1 $2", CrcAliasVariables.Substitute("$1 $2", Oakland));
    }

    [Fact]
    public void TextWithoutVariables_IsUnchanged()
    {
        const string Text = "DESIGNATOR: C172 | RECAT: I | TYPE: PROP (1 ENGINE) / SMALL";

        Assert.Equal(Text, CrcAliasVariables.Substitute(Text, Oakland));
    }
}

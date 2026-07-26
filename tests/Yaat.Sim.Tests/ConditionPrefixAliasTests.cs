using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Every keyword the parser treats as a condition prefix must actually work in condition position
/// through the client canonicalizer, which runs before anything is sent to the server.
///
/// The gap this closes: <c>ONH</c> was documented as an alias of <c>ONHO</c>, accepted by the server's
/// CommandParser, and listed in <see cref="CommandSchemeParser.ConditionPrefixes"/> — but the
/// canonicalizer's compound sniff and block parser knew only <c>ONHO</c>, so the command failed on the
/// client and was never sent. Completeness tests checked that each command type has <em>some</em> alias,
/// not that each alias works in every grammatical position it is documented for.
/// </summary>
public class ConditionPrefixAliasTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    public ConditionPrefixAliasTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// One representative use per condition prefix. GIVEWAY/BEHIND/GW are conditions only in their
    /// three-token form (callsign + ground command); with fewer tokens they are standalone commands.
    /// </summary>
    [Theory]
    [InlineData("AT", "AT LIVVY DEL")]
    [InlineData("ATFN", "ATFN 5 FH 270")]
    [InlineData("LV", "LV 100 CM 150")]
    [InlineData("ONHO", "ONHO CM 360")]
    [InlineData("ONH", "ONH CM 360")]
    [InlineData("GIVEWAY", "GIVEWAY UAL123 TAXI A")]
    [InlineData("BEHIND", "BEHIND UAL123 TAXI A")]
    [InlineData("GW", "GW UAL123 TAXI A")]
    public void EveryConditionPrefix_ParsesInConditionPosition(string prefix, string input)
    {
        Assert.Contains(prefix, CommandSchemeParser.ConditionPrefixes);

        var result = CommandSchemeParser.ParseCompound(input, Scheme);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.CanonicalString), $"'{input}' produced an empty canonical string");
    }

    /// <summary>Guards the table above against a prefix being added to the parser but not covered here.</summary>
    [Fact]
    public void ConditionPrefixTable_CoversEveryPrefix()
    {
        string[] covered = ["AT", "ATFN", "LV", "ONHO", "ONH", "GIVEWAY", "BEHIND", "GW"];
        Assert.Equal(CommandSchemeParser.ConditionPrefixes.Order(StringComparer.OrdinalIgnoreCase), covered.Order(StringComparer.OrdinalIgnoreCase));
    }
}

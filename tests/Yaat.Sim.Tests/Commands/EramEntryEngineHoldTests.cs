using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The live check of an HM / QH hold entry,
/// <see cref="EramEntryEngine.ParseHoldFields(IReadOnlyList{string}, NavigationDatabase, out EramHoldEntry)"/>: a field 21
/// location resolves as field 68 does (<see cref="EramFixResolver.ParseLocation"/>) against real navigation data, and
/// anything that does not is <c>COFIE FORMAT</c> naming the field.
/// </summary>
public class EramEntryEngineHoldTests
{
    [Theory]
    [InlineData("OAK090010")]
    [InlineData("3730N/12200W")]
    public void ParseHoldFields_AcceptsAKnownLocation(string location)
    {
        CommandResult? refusal = EramEntryEngine.ParseHoldFields([location], RealNavDb(), out EramHoldEntry entry);

        Assert.Null(refusal);
        Assert.Equal(EramHoldDataKind.Location, entry.Data?.Kind);
        Assert.Equal(location, entry.Data?.Location);
    }

    [Theory]
    [InlineData("ZZZZZ")] // not in the navigation data
    [InlineData("OAK000010")] // radial 000
    public void ParseHoldFields_RefusesALocationThatDoesNotResolve(string location)
    {
        CommandResult? refusal = EramEntryEngine.ParseHoldFields([location], RealNavDb(), out _);

        Assert.NotNull(refusal);
        Assert.False(refusal.Success);
        Assert.Equal($"{EramEntryErrors.CofieFormat} {location}", refusal.Message);
    }

    [Fact]
    public void ParseRecordedHoldFields_TrustsTheRecordedLocation()
    {
        Assert.Null(EramEntryEngine.ParseRecordedHoldFields(["ZZZZZ"], out EramHoldEntry entry));
        Assert.Equal("ZZZZZ", entry.Data?.Location);
    }

    private static NavigationDatabase RealNavDb()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        Assert.NotNull(navDb);
        return navDb;
    }
}

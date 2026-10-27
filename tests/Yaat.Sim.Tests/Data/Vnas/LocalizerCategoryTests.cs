using Xunit;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Data.Vnas;

/// <summary>
/// Each runway end's ILS category comes from the CIFP localizer records (section P, subsection I): OAK's IINB serves 30
/// as CAT III, IAAZ serves 12 and IOAK serves 28R as CAT I, and 28L and 15 have no localizer. An airport whose ICAO id
/// is not "K" plus its FAA id (PANC, ANC) is found through the navdata airport's own ICAO id.
/// </summary>
public class LocalizerCategoryTests
{
    public LocalizerCategoryTests() => TestVnasData.EnsureInitialized();

    [Theory]
    [InlineData("OAK", "30", 3)]
    [InlineData("OAK", "12", 1)]
    [InlineData("OAK", "28R", 1)]
    [InlineData("KOAK", "30", 3)]
    [InlineData("ANC", "07R", 3)] // PANC IANC: a non-K ICAO id asked by its FAA id
    [InlineData("ANC", "15", 1)] // PANC IBSC
    public void LocalizerCategory_ReadsTheEndsCategory(string airport, string runwayEnd, int expected) =>
        Assert.Equal(expected, TestVnasData.NavigationDb!.LocalizerCategory(airport, runwayEnd));

    [Theory]
    [InlineData("OAK", "28L")]
    [InlineData("OAK", "15")]
    public void LocalizerCategory_NoLocalizer_IsNull(string airport, string runwayEnd) =>
        Assert.Null(TestVnasData.NavigationDb!.LocalizerCategory(airport, runwayEnd));
}

using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Tests;

public class FixSuggesterTests
{
    public FixSuggesterTests()
    {
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting());
    }

    // -------------------------------------------------------------------------
    // GetTextBeforeLastWord
    // -------------------------------------------------------------------------

    [Fact]
    public void GetTextBeforeLastWord_MultipleWords_ReturnsPrefix()
    {
        Assert.Equal("AAL123 DCT ", FixSuggester.GetTextBeforeLastWord("AAL123 DCT SUN"));
    }

    [Fact]
    public void GetTextBeforeLastWord_SingleWord_ReturnsEmpty()
    {
        Assert.Equal("", FixSuggester.GetTextBeforeLastWord("DCT"));
    }

    [Fact]
    public void GetTextBeforeLastWord_TrailingSpace_ReturnsFullText()
    {
        Assert.Equal("DCT SUNOL ", FixSuggester.GetTextBeforeLastWord("DCT SUNOL "));
    }

    [Fact]
    public void GetTextBeforeLastWord_Empty_ReturnsEmpty()
    {
        Assert.Equal("", FixSuggester.GetTextBeforeLastWord(""));
    }

    // -------------------------------------------------------------------------
    // CollectRouteFixNames
    // -------------------------------------------------------------------------

    [Fact]
    public void CollectRouteFixNames_DepartureAndDestination_Included()
    {
        var aircraft = new AircraftModel { Departure = "OAK", Destination = "LAX" };

        var fixes = FixSuggester.CollectRouteFixNames(aircraft);

        Assert.Contains("OAK", fixes);
        Assert.Contains("LAX", fixes);
    }

    [Fact]
    public void CollectRouteFixNames_NullFields_ReturnsEmpty()
    {
        var aircraft = new AircraftModel();

        var fixes = FixSuggester.CollectRouteFixNames(aircraft);

        Assert.Empty(fixes);
    }

    [Fact]
    public void CollectRouteFixNames_EmptyStrings_ReturnsEmpty()
    {
        var aircraft = new AircraftModel { Departure = "", Destination = "  " };

        var fixes = FixSuggester.CollectRouteFixNames(aircraft);

        Assert.Empty(fixes);
    }

    [Fact]
    public void CollectRouteFixNames_DuplicateAirports_Deduped()
    {
        var aircraft = new AircraftModel { Departure = "OAK", Destination = "OAK" };

        var fixes = FixSuggester.CollectRouteFixNames(aircraft);

        Assert.Single(fixes);
        Assert.Contains("OAK", fixes);
    }

    // -------------------------------------------------------------------------
    // TryAddFixSuggestions
    // -------------------------------------------------------------------------

    [Fact]
    public void TryAddFixSuggestions_NoDctPattern_ReturnsFalse()
    {
        var suggestions = new ObservableCollection<SuggestionItem>();
        var scheme = new CommandScheme { Patterns = new Dictionary<CanonicalCommandType, CommandPattern>() };

        var result = FixSuggester.TryAddFixSuggestions("DCT SUN", "DCT SUN", null, scheme, suggestions, 10);

        Assert.False(result);
    }

    [Fact]
    public void TryAddFixSuggestions_NotDctCommand_ReturnsFalse()
    {
        var suggestions = new ObservableCollection<SuggestionItem>();
        var scheme = CommandScheme.Default();

        var result = FixSuggester.TryAddFixSuggestions("FH 180", "FH 180", null, scheme, suggestions, 10);

        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // AddFixSuggestions — empty NavigationDatabase, no aircraft
    // -------------------------------------------------------------------------

    [Fact]
    public void AddFixSuggestions_EmptyNavDb_NoSuggestions()
    {
        var suggestions = new ObservableCollection<SuggestionItem>();

        FixSuggester.AddFixSuggestions("SUN", "", null, suggestions, 10);

        Assert.Empty(suggestions);
    }
}

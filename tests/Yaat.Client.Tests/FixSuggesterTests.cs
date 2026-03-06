using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class FixSuggesterTests
{
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

        var fixes = FixSuggester.CollectRouteFixNames(aircraft, null);

        Assert.Contains("OAK", fixes);
        Assert.Contains("LAX", fixes);
    }

    [Fact]
    public void CollectRouteFixNames_NullFields_ReturnsEmpty()
    {
        var aircraft = new AircraftModel();

        var fixes = FixSuggester.CollectRouteFixNames(aircraft, null);

        Assert.Empty(fixes);
    }

    [Fact]
    public void CollectRouteFixNames_EmptyStrings_ReturnsEmpty()
    {
        var aircraft = new AircraftModel { Departure = "", Destination = "  " };

        var fixes = FixSuggester.CollectRouteFixNames(aircraft, null);

        Assert.Empty(fixes);
    }

    [Fact]
    public void CollectRouteFixNames_DuplicateAirports_Deduped()
    {
        var aircraft = new AircraftModel { Departure = "OAK", Destination = "OAK" };

        var fixes = FixSuggester.CollectRouteFixNames(aircraft, null);

        Assert.Single(fixes);
        Assert.Contains("OAK", fixes);
    }

    // -------------------------------------------------------------------------
    // TryAddFixSuggestions — no FixDatabase
    // -------------------------------------------------------------------------

    [Fact]
    public void TryAddFixSuggestions_NoFixDb_ReturnsFalse()
    {
        var suggestions = new ObservableCollection<SuggestionItem>();
        var scheme = CommandScheme.Default();

        var result = FixSuggester.TryAddFixSuggestions("DCT SUN", "DCT SUN", null, scheme, suggestions, null, 10);

        Assert.False(result);
    }

    [Fact]
    public void TryAddFixSuggestions_NoDctPattern_ReturnsFalse()
    {
        var suggestions = new ObservableCollection<SuggestionItem>();
        // Empty scheme with no patterns
        var scheme = new CommandScheme { Patterns = new Dictionary<Sim.Commands.CanonicalCommandType, CommandPattern>() };

        var result = FixSuggester.TryAddFixSuggestions("DCT SUN", "DCT SUN", null, scheme, suggestions, null, 10);

        Assert.False(result);
    }

    [Fact]
    public void TryAddFixSuggestions_NotDctCommand_ReturnsFalse()
    {
        var suggestions = new ObservableCollection<SuggestionItem>();
        var scheme = CommandScheme.Default();

        var result = FixSuggester.TryAddFixSuggestions("FH 180", "FH 180", null, scheme, suggestions, null, 10);

        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // AddFixSuggestions — no FixDatabase, no aircraft
    // -------------------------------------------------------------------------

    [Fact]
    public void AddFixSuggestions_NoFixDb_NoSuggestions()
    {
        var suggestions = new ObservableCollection<SuggestionItem>();

        FixSuggester.AddFixSuggestions("SUN", "", null, suggestions, null, 10);

        Assert.Empty(suggestions);
    }
}

using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// TAXI and RTAXI offer the standalone modifiers — RWY, @ and $ — at their first argument slot, beside the
/// taxiways, because <c>TAXI RWY 28R</c> is the canonical no-route taxi and @/$ name the destination. The
/// modifiers that need a route first (HS, CROSS, NODEL, EXP) stay out of that slot, and every later slot
/// offers the full set as before.
/// </summary>
public class ArgumentSuggesterTaxiSlotOneTests
{
    private const string Airport = "KOAK";

    /// <summary>The real OAK ground layout's taxiway names, as the client's autocomplete list takes them.</summary>
    private static readonly Lazy<IReadOnlyList<string>> OakTaxiways = new(() =>
        GroundLayoutSpeechNames.TaxiwayNames(AirportLayoutFile.Load(Path.Combine("TestData", "oak.geojson"), Airport))
    );

    private static ObservableCollection<SuggestionItem> Suggest(string text, IReadOnlyCollection<string> taxiwayNames, int maxSuggestions)
    {
        var scheme = CommandScheme.Default();
        CommandInputParseResult? parsed = CommandInputController.ParseCommandInput(text, text.Length, scheme);
        Assert.NotNull(parsed);

        var suggestions = new ObservableCollection<SuggestionItem>();
        ArgumentSuggester.TryAddArgumentSuggestions(
            parsed,
            text,
            targetAircraft: null,
            aircraft: [],
            suggestions,
            primaryAirportId: Airport,
            taxiwayNames,
            spotNames: [],
            standNames: [],
            maxSuggestions
        );
        return suggestions;
    }

    [Fact]
    public void TaxiSlotOne_OffersRwyAtAndDollar()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI ", ["A", "B"], maxSuggestions: 20);

        Assert.Contains(suggestions, s => s.Text == "RWY");
        Assert.Contains(suggestions, s => s.Text == "@");
        Assert.Contains(suggestions, s => s.Text == "$");
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void RtaxiSlotOne_DoesNotOfferHsCrossNodelExp()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("RTAXI ", ["A", "B"], maxSuggestions: 20);

        // The standalone ones are offered, so the absence below is not an empty list.
        Assert.Contains(suggestions, s => s.Text == "RWY");
        Assert.DoesNotContain(suggestions, s => (s.Text == "HS") || (s.Text == "CROSS") || (s.Text == "NODEL") || (s.Text == "EXP"));
    }

    [Fact]
    public void TaxiSlotOne_AtTheAppCap_KeepsRoomForTheStandaloneModifiers()
    {
        // The app's dropdown holds ten rows, so a route list longer than that would fill every one of them
        // and hide TAXI RWY 28R entirely. The taxiways stop short instead: seven of them, then RWY, @ and $.
        IReadOnlyList<string> oakTaxiways = OakTaxiways.Value;
        Assert.True(oakTaxiways.Count >= 12, $"The OAK layout fixture has {oakTaxiways.Count} taxiway names; the cap case needs 12.");
        string[] twelve = [.. oakTaxiways.Take(12)];

        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI ", twelve, maxSuggestions: 10);

        Assert.Equal(10, suggestions.Count);
        Assert.Equal(7, suggestions.Count(s => s.Description == "Taxiway"));
        Assert.Contains(suggestions, s => s.Text == "RWY");
        Assert.Contains(suggestions, s => s.Text == "@");
        Assert.Contains(suggestions, s => s.Text == "$");
    }

    [Fact]
    public void RtaxiSlotOne_OffersRwyAtAndDollar()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("RTAXI ", ["A", "B"], maxSuggestions: 20);

        Assert.Contains(suggestions, s => s.Text == "RWY");
        Assert.Contains(suggestions, s => s.Text == "@");
        Assert.Contains(suggestions, s => s.Text == "$");
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void TaxiSlotTwo_AfterARoute_OffersEveryModifierAsToday()
    {
        // The slot after one route token is unchanged: the route's taxiways plus all seven modifier
        // keywords. The set was captured against the code before slot 0 changed, and pins that the
        // standalone flag narrows slot 0 alone.
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI A ", ["A", "B"], maxSuggestions: 20);

        string[] expected = ["$", "@", "A", "B", "CROSS", "EXP", "HS", "NODEL", "RWY"];
        Assert.Equal(expected, suggestions.Select(s => s.Text).OrderBy(t => t, StringComparer.Ordinal));
    }
}

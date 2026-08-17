using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// Autocomplete for taxiway-typed arguments: a "taxiway/runway" or "taxiway name" parameter offers
/// the loaded ground layout's taxiway names, not just runways. `HS `'s dropdown used to be
/// runway-only because the hint check substring-matched "runway" inside "taxiway/runway" and no
/// taxiway source existed.
/// </summary>
public class ArgumentSuggesterTaxiwayTests
{
    private static ObservableCollection<SuggestionItem> Suggest(string text, IReadOnlyCollection<string> taxiwayNames)
    {
        var scheme = CommandScheme.Default();
        var parsed = CommandInputController.ParseCommandInput(text, text.Length, scheme);
        Assert.NotNull(parsed);

        var suggestions = new ObservableCollection<SuggestionItem>();
        ArgumentSuggester.TryAddArgumentSuggestions(
            parsed,
            text,
            targetAircraft: null,
            aircraft: [],
            suggestions,
            primaryAirportId: null,
            taxiwayNames,
            maxSuggestions: 20
        );
        return suggestions;
    }

    [Fact]
    public void HoldShort_ArgumentSlot_OffersTaxiways()
    {
        var suggestions = Suggest("HS ", ["A", "B", "J"]);
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
        Assert.Contains(suggestions, s => s.Text == "J");
    }

    [Fact]
    public void HoldShort_PartialToken_FiltersTaxiways()
    {
        var suggestions = Suggest("HS J", ["A", "B", "J", "J1"]);
        Assert.Contains(suggestions, s => s.Text == "J");
        Assert.Contains(suggestions, s => s.Text == "J1");
        Assert.DoesNotContain(suggestions, s => s.Text == "A");
    }

    [Fact]
    public void Taxi_RouteSlot_OffersTaxiways()
    {
        var suggestions = Suggest("TAXI ", ["A", "B"]);
        Assert.Contains(suggestions, s => (s.Text == "B") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void CrossModifier_HsSlot_OffersTaxiways()
    {
        var suggestions = Suggest("CROSS 28R HS ", ["A", "B"]);
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void NoTaxiwayNames_NoTaxiwaySuggestions()
    {
        var suggestions = Suggest("HS ", []);
        Assert.DoesNotContain(suggestions, s => s.Description == "Taxiway");
    }
}

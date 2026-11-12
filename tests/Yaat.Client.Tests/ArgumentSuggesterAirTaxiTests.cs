using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Client.Tests;

/// <summary>
/// Autocomplete for the <c>ATXI</c> destination: a runway, an <c>@</c> stand (parking or helipad) or a <c>$</c> spot,
/// the markers the air taxi takes. A bare taxiway name is never offered, because ATXI refuses one; TAXI's route slot
/// keeps offering taxiways.
/// </summary>
public class ArgumentSuggesterAirTaxiTests
{
    private const string Airport = "KOAK";

    public ArgumentSuggesterAirTaxiTests()
    {
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting(runways: [Runway()]));
    }

    private static RunwayInfo Runway() =>
        new()
        {
            AirportId = Airport,
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = 37.72,
            Lon1 = -122.22,
            Elevation1Ft = 9,
            TrueHeading1 = new TrueHeading(284),
            Lat2 = 37.73,
            Lon2 = -122.25,
            Elevation2Ft = 9,
            TrueHeading2 = new TrueHeading(104),
            WidthFt = 150,
        };

    private static ObservableCollection<SuggestionItem> Suggest(string text)
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
            taxiwayNames: ["A", "B"],
            spotNames: ["S7"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        return suggestions;
    }

    [Fact]
    public void AirTaxi_DestinationSlot_OffersRunwaysStandsAndSpots_NoTaxiways()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("ATXI ");

        Assert.Contains(suggestions, s => (s.Text == "28R") && (s.Description == "Runway"));
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.Contains(suggestions, s => s.Text == "$S7");
        Assert.DoesNotContain(suggestions, s => (s.Text == "A") || (s.Text == "B") || (s.Description == "Taxiway"));
    }

    [Fact]
    public void AirTaxi_AtSigil_OffersStandsOnly()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("ATXI @");

        Assert.Contains(suggestions, s => (s.Text == "@B27") && (s.InsertText == "ATXI @B27 "));
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("S7", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AirTaxi_DollarSigil_OffersSpotsOnly()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("ATXI $");

        Assert.Contains(suggestions, s => (s.Text == "$S7") && (s.InsertText == "ATXI $S7 "));
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Taxi_RouteSlot_StillOffersBareTaxiways()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI ");

        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
        Assert.Contains(suggestions, s => (s.Text == "B") && (s.Description == "Taxiway"));
    }
}

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
/// Autocomplete for taxiway-typed arguments: a "taxiway/runway" or "taxiway name" parameter offers
/// the loaded ground layout's taxiway names, not just runways. `HS `'s dropdown used to be
/// runway-only because the hint check substring-matched "runway" inside "taxiway/runway" and no
/// taxiway source existed.
/// </summary>
public class ArgumentSuggesterTaxiwayTests
{
    private const string Airport = "KOAK";

    /// <summary>
    /// A primary airport with one runway, so a slot that offers runways produces something: an assertion
    /// that a later slot offers nothing would otherwise pass on an empty navigation database alone.
    /// </summary>
    public ArgumentSuggesterTaxiwayTests()
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

    private static ObservableCollection<SuggestionItem> Suggest(
        string text,
        IReadOnlyCollection<string> taxiwayNames,
        IReadOnlyCollection<string> spotNames,
        IReadOnlyCollection<string> standNames,
        int maxSuggestions
    )
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
            spotNames,
            standNames,
            maxSuggestions
        );
        return suggestions;
    }

    [Fact]
    public void HoldShort_ArgumentSlot_OffersTaxiways()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("HS ", ["A", "B", "J"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
        Assert.Contains(suggestions, s => s.Text == "J");
    }

    [Fact]
    public void HoldShort_PartialToken_FiltersTaxiways()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("HS J", ["A", "B", "J", "J1"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "J");
        Assert.Contains(suggestions, s => s.Text == "J1");
        Assert.DoesNotContain(suggestions, s => s.Text == "A");
    }

    [Fact]
    public void Taxi_RouteSlot_OffersTaxiways()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI ", ["A", "B"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => (s.Text == "B") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void CrossModifier_HsSlot_OffersTaxiways()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("CROSS 28R HS ", ["A", "B"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void NoTaxiwayNames_NoTaxiwaySuggestions()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("HS ", [], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Description == "Taxiway");
    }

    [Fact]
    public void Taxi_AtSigil_OffersStandNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXI C D @",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["NEW1", "B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@NEW1");
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.Contains(suggestions, s => s.InsertText == "TAXI C D @NEW1 ");
        Assert.Contains(suggestions, s => s.InsertText == "TAXI C D @B27 ");
    }

    [Fact]
    public void Taxi_AtPartial_FiltersStandNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXI C D @B",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["NEW1", "B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.DoesNotContain(suggestions, s => s.Text == "@NEW1");
    }

    [Fact]
    public void Taxi_AtSigil_NeverOffersSpotNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXI C D @",
            taxiwayNames: [],
            spotNames: ["S7"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("S7", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Taxi_DollarSigil_OffersSpotNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXI C D $",
            taxiwayNames: [],
            spotNames: ["S7", "S9"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "$S7");
        Assert.Contains(suggestions, s => s.Text == "$S9");
        Assert.Contains(suggestions, s => s.InsertText == "TAXI C D $S7 ");
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Push_AtSigil_OffersStandNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "PUSH @",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.Contains(suggestions, s => s.InsertText == "PUSH @B27 ");
    }

    [Fact]
    public void Push_AfterAStand_OffersNoFacing()
    {
        // PUSH @stand parks on the stand's own heading; the parser refuses any facing after the stand. A spot
        // still takes a facing taxiway.
        ObservableCollection<SuggestionItem> afterStand = Suggest(
            "PUSH @B27 ",
            ["A", "TE"],
            spotNames: ["7A"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        ObservableCollection<SuggestionItem> afterStandPartial = Suggest(
            "PUSH @B27 T",
            ["A", "TE"],
            spotNames: ["7A"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        ObservableCollection<SuggestionItem> afterSpot = Suggest(
            "PUSH $7A ",
            ["A", "TE"],
            spotNames: ["7A"],
            standNames: ["B27"],
            maxSuggestions: 20
        );

        Assert.Empty(afterStand);
        Assert.Empty(afterStandPartial);
        Assert.Contains(afterSpot, s => (s.Text == "TE") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void Taxi_AtSigil_NoStandsLoaded_NoSuggestions()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI C D @", taxiwayNames: [], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@') && (s.Text.Length > 1));
    }

    [Fact]
    public void Taxi_HsRegion_AtSigil_OffersNoStands()
    {
        // Inside HS's argument region the `@` belongs to the hold-short target, which the server's
        // HoldShortTarget.TryParse rejects as a parking — the stand flyout must stay out of it. The bare
        // `@` modifier keyword may still be echoed; what must never appear is a stand name behind it.
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI A HS @", ["A", "B"], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@') && (s.Text.Length > 1));
    }

    [Fact]
    public void Taxi_RwyRegion_AtSigil_OffersNoStands()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXI A RWY @",
            ["A", "B"],
            spotNames: [],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@') && (s.Text.Length > 1));
    }

    [Fact]
    public void Push_AtSigilAfterFirstToken_OffersNoStands()
    {
        // ParsePushback only strips @parking/$spot from the first token and refuses a later one outright,
        // so offering a stand there would hand the instructor a command the parser rejects.
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "PUSH TE @",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@'));
    }

    [Fact]
    public void Taxi_AtSigil_ClaimsSlot()
    {
        // The sigil owns the token even with no stands loaded: a bare `@` must not fall back to taxiways.
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI C D @", ["A", "B"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Empty(suggestions);
    }

    [Fact]
    public void Taxi_BareName_NeverOffersStands()
    {
        // A route token without a sigil stays a taxiway: every slot offers taxiway B, and no slot
        // turns a bare name into the stand B27.
        ObservableCollection<SuggestionItem> firstSlot = Suggest("TAXI B", ["B"], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.Contains(firstSlot, s => (s.Text == "B") && (s.Description == "Taxiway"));
        Assert.DoesNotContain(firstSlot, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));

        ObservableCollection<SuggestionItem> laterSlot = Suggest("TAXI C D B", ["B"], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.Contains(laterSlot, s => (s.Text == "B") && (s.Description == "Taxiway"));
        Assert.DoesNotContain(laterSlot, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Taxi_LaterRouteSlot_OffersTaxiways()
    {
        // The route is a list, so every slot past the first keeps offering taxiways — alongside the
        // modifier keywords, which stay available once the route has started.
        ObservableCollection<SuggestionItem> emptySlot = Suggest("TAXI C D ", ["A", "B", "B1"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(emptySlot, s => (s.Text == "A") && (s.Description == "Taxiway"));
        Assert.Contains(emptySlot, s => (s.Text == "B") && (s.Description == "Taxiway"));
        Assert.Contains(emptySlot, s => s.Text == "HS");

        ObservableCollection<SuggestionItem> partialSlot = Suggest("TAXI C D B", ["A", "B", "B1"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(partialSlot, s => s.Text == "B");
        Assert.Contains(partialSlot, s => s.Text == "B1");
        Assert.DoesNotContain(partialSlot, s => s.Text == "A");
    }

    [Fact]
    public void Taxi_FirstSlot_OffersNoModifierKeywords()
    {
        // TAXI's first slot is the route's, by design: the keywords would crowd out the taxiway list
        // exactly where the instructor is picking the first taxiway. RWY, @ and $ typed there still parse.
        ObservableCollection<SuggestionItem> suggestions = Suggest("TAXI ", ["A", "B"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
        Assert.DoesNotContain(suggestions, s => (s.Text == "RWY") || (s.Text == "HS") || (s.Text == "CROSS") || (s.Text == "NODEL"));
    }

    [Fact]
    public void Taxi_AtPartial_MatchesCaseInsensitively()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXI C D @b",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@B27");
    }

    [Fact]
    public void TaxiAuto_AtSigil_OffersStandNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXIAUTO @",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["NEW1", "B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@NEW1");
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.Contains(suggestions, s => s.InsertText == "TAXIAUTO @B27 ");
    }

    [Fact]
    public void TaxiAll_AtSigil_OffersStandNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXIALL @",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.Contains(suggestions, s => s.InsertText == "TAXIALL @B27 ");
    }

    [Fact]
    public void TaxiAuto_DollarSigil_OffersSpotNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXIAUTO $",
            taxiwayNames: [],
            spotNames: ["S7", "S9"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "$S7");
        Assert.Contains(suggestions, s => s.Text == "$S9");
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaxiAuto_AtSigil_NeverOffersSpotNames()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXIAUTO @",
            taxiwayNames: [],
            spotNames: ["S7"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("S7", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaxiAuto_AfterTheDestination_OffersNothing()
    {
        // TAXIAUTO takes exactly one destination token, so nothing follows it: neither a second name nor a
        // sigil, whose stand/spot flyout belongs to the leading token alone. The destination slot itself
        // offers the airport's runways, which is what makes the empty later slot an assertion.
        ObservableCollection<SuggestionItem> destinationSlot = Suggest(
            "TAXIAUTO ",
            ["A", "TE"],
            spotNames: ["7A"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        ObservableCollection<SuggestionItem> afterRunway = Suggest(
            "TAXIAUTO 28R ",
            ["A", "TE"],
            spotNames: ["7A"],
            standNames: ["B27"],
            maxSuggestions: 20
        );
        ObservableCollection<SuggestionItem> afterStand = Suggest(
            "TAXIAUTO @B27 @",
            ["A", "TE"],
            spotNames: ["7A"],
            standNames: ["B27"],
            maxSuggestions: 20
        );

        Assert.Contains(destinationSlot, s => (s.Text == "28R") && (s.Description == "Runway"));
        Assert.Empty(afterRunway);
        Assert.Empty(afterStand);
    }

    [Fact]
    public void Taxi_AtSigil_RespectsMaxSuggestions()
    {
        ObservableCollection<SuggestionItem> suggestions = Suggest(
            "TAXI C D @",
            taxiwayNames: [],
            spotNames: [],
            standNames: ["B21", "B22", "B23", "B24", "B25"],
            maxSuggestions: 3
        );
        Assert.Equal(3, suggestions.Count(s => s.Text.StartsWith('@')));
    }
}

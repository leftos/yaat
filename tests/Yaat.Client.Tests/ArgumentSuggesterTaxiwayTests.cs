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
    private static ObservableCollection<SuggestionItem> Suggest(
        string text,
        IReadOnlyCollection<string> taxiwayNames,
        IReadOnlyCollection<string> spotNames,
        IReadOnlyCollection<string> standNames,
        int maxSuggestions
    )
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
            spotNames,
            standNames,
            maxSuggestions
        );
        return suggestions;
    }

    [Fact]
    public void HoldShort_ArgumentSlot_OffersTaxiways()
    {
        var suggestions = Suggest("HS ", ["A", "B", "J"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
        Assert.Contains(suggestions, s => s.Text == "J");
    }

    [Fact]
    public void HoldShort_PartialToken_FiltersTaxiways()
    {
        var suggestions = Suggest("HS J", ["A", "B", "J", "J1"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "J");
        Assert.Contains(suggestions, s => s.Text == "J1");
        Assert.DoesNotContain(suggestions, s => s.Text == "A");
    }

    [Fact]
    public void Taxi_RouteSlot_OffersTaxiways()
    {
        var suggestions = Suggest("TAXI ", ["A", "B"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => (s.Text == "B") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void CrossModifier_HsSlot_OffersTaxiways()
    {
        var suggestions = Suggest("CROSS 28R HS ", ["A", "B"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Contains(suggestions, s => (s.Text == "A") && (s.Description == "Taxiway"));
    }

    [Fact]
    public void NoTaxiwayNames_NoTaxiwaySuggestions()
    {
        var suggestions = Suggest("HS ", [], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Description == "Taxiway");
    }

    [Fact]
    public void Taxi_AtSigil_OffersStandNames()
    {
        var suggestions = Suggest("TAXI C D @", taxiwayNames: [], spotNames: [], standNames: ["NEW1", "B27"], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "@NEW1");
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.Contains(suggestions, s => s.InsertText == "TAXI C D @NEW1 ");
        Assert.Contains(suggestions, s => s.InsertText == "TAXI C D @B27 ");
    }

    [Fact]
    public void Taxi_AtPartial_FiltersStandNames()
    {
        var suggestions = Suggest("TAXI C D @B", taxiwayNames: [], spotNames: [], standNames: ["NEW1", "B27"], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.DoesNotContain(suggestions, s => s.Text == "@NEW1");
    }

    [Fact]
    public void Taxi_AtSigil_NeverOffersSpotNames()
    {
        var suggestions = Suggest("TAXI C D @", taxiwayNames: [], spotNames: ["S7"], standNames: ["B27"], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("S7", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Taxi_DollarSigil_OffersSpotNames()
    {
        var suggestions = Suggest("TAXI C D $", taxiwayNames: [], spotNames: ["S7", "S9"], standNames: ["B27"], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "$S7");
        Assert.Contains(suggestions, s => s.Text == "$S9");
        Assert.Contains(suggestions, s => s.InsertText == "TAXI C D $S7 ");
        Assert.DoesNotContain(suggestions, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Push_AtSigil_OffersStandNames()
    {
        var suggestions = Suggest("PUSH @", taxiwayNames: [], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "@B27");
        Assert.Contains(suggestions, s => s.InsertText == "PUSH @B27 ");
    }

    [Fact]
    public void Taxi_AtSigil_NoStandsLoaded_NoSuggestions()
    {
        var suggestions = Suggest("TAXI C D @", taxiwayNames: [], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@') && (s.Text.Length > 1));
    }

    [Fact]
    public void Taxi_HsRegion_AtSigil_OffersNoStands()
    {
        // Inside HS's argument region the `@` belongs to the hold-short target, which the server's
        // HoldShortTarget.TryParse rejects as a parking — the stand flyout must stay out of it. The bare
        // `@` modifier keyword may still be echoed; what must never appear is a stand name behind it.
        var suggestions = Suggest("TAXI A HS @", ["A", "B"], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@') && (s.Text.Length > 1));
    }

    [Fact]
    public void Taxi_RwyRegion_AtSigil_OffersNoStands()
    {
        var suggestions = Suggest("TAXI A RWY @", ["A", "B"], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@') && (s.Text.Length > 1));
    }

    [Fact]
    public void Push_AtSigilAfterFirstToken_OffersNoStands()
    {
        // ParsePushback only strips @parking/$spot from the first token; later it would be a facing taxiway.
        var suggestions = Suggest("PUSH TE @", taxiwayNames: [], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.DoesNotContain(suggestions, s => s.Text.StartsWith('@'));
    }

    [Fact]
    public void Taxi_AtSigil_ClaimsSlot()
    {
        // The sigil owns the token even with no stands loaded: a bare `@` must not fall back to taxiways.
        var suggestions = Suggest("TAXI C D @", ["A", "B"], spotNames: [], standNames: [], maxSuggestions: 20);
        Assert.Empty(suggestions);
    }

    [Fact]
    public void Taxi_BareName_NeverOffersStands()
    {
        // A route token without a sigil stays a taxiway: the first slot offers taxiway B, and no slot
        // turns a bare name into the stand B27.
        var firstSlot = Suggest("TAXI B", ["B"], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.Contains(firstSlot, s => (s.Text == "B") && (s.Description == "Taxiway"));
        Assert.DoesNotContain(firstSlot, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));

        var laterSlot = Suggest("TAXI C D B", ["B"], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.DoesNotContain(laterSlot, s => s.Text.Contains("B27", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Taxi_AtPartial_MatchesCaseInsensitively()
    {
        var suggestions = Suggest("TAXI C D @b", taxiwayNames: [], spotNames: [], standNames: ["B27"], maxSuggestions: 20);
        Assert.Contains(suggestions, s => s.Text == "@B27");
    }

    [Fact]
    public void Taxi_AtSigil_RespectsMaxSuggestions()
    {
        var suggestions = Suggest("TAXI C D @", taxiwayNames: [], spotNames: [], standNames: ["B21", "B22", "B23", "B24", "B25"], maxSuggestions: 3);
        Assert.Equal(3, suggestions.Count(s => s.Text.StartsWith('@')));
    }
}

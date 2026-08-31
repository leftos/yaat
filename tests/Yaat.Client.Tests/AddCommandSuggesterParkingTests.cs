using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// Autocomplete for the ADD command's <c>@{spot}</c> position variant: typing <c>@</c> in the
/// position slot offers the primary airport's parking/helipad/spot names from the loaded ground
/// layout. It used to offer global navdata fixes, which never matched what the server's
/// spawn-at-parking lookup resolves.
/// </summary>
public class AddCommandSuggesterParkingTests
{
    private static ObservableCollection<SuggestionItem> Suggest(string text, IReadOnlyCollection<string> parkingNames)
    {
        var scheme = CommandScheme.Default();
        var parsed = CommandInputController.ParseCommandInput(text, text.Length, scheme);
        Assert.NotNull(parsed);

        var suggestions = new ObservableCollection<SuggestionItem>();
        AddCommandSuggester.TryAddAddArgumentSuggestions(parsed, text, scheme, suggestions, "OAK", parkingNames, maxSuggestions: 20);
        return suggestions;
    }

    [Fact]
    public void PositionSlot_AtPrefix_OffersParkingNames()
    {
        var suggestions = Suggest("ADD I S+ J @", ["22", "G1", "H5"]);
        Assert.Contains(suggestions, s => s.Text == "@22");
        Assert.Contains(suggestions, s => s.Text == "@G1");
        Assert.Contains(suggestions, s => s.Text == "@H5");
    }

    [Fact]
    public void PositionSlot_AtPartial_FiltersParkingNames()
    {
        var suggestions = Suggest("ADD I S+ J @22", ["22", "2201", "G1"]);
        Assert.Contains(suggestions, s => s.Text == "@22");
        Assert.Contains(suggestions, s => s.Text == "@2201");
        Assert.DoesNotContain(suggestions, s => s.Text == "@G1");
    }

    [Fact]
    public void PositionSlot_AtPrefix_InsertsSpotWithAtPrefix()
    {
        var suggestions = Suggest("ADD I S+ J @2", ["22"]);
        var item = Assert.Single(suggestions);
        Assert.Equal("ADD I S+ J @22 ", item.InsertText);
    }

    [Fact]
    public void PositionSlot_NoLayoutLoaded_NoSuggestions()
    {
        var suggestions = Suggest("ADD I S+ J @2", []);
        Assert.Empty(suggestions);
    }

    [Fact]
    public void PositionSlot_AtPrefix_NeverOffersFixes()
    {
        var suggestions = Suggest("ADD I S+ J @22", ["22"]);
        Assert.DoesNotContain(suggestions, s => (s.Kind == SuggestionKind.Fix) || (s.Kind == SuggestionKind.RouteFix));
    }
}

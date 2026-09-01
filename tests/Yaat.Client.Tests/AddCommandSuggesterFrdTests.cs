using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Tests;

/// <summary>
/// Live FRD preview in the ADD command's <c>@</c> position slot (follow-up to issue #413): typing
/// a fix/radial/distance string after <c>@</c> shows a validation row — the parsed breakdown when
/// the anchor fix resolves, "not found" when it doesn't, a prompt for the missing distance digits,
/// and a radial-range error for an impossible azimuth. Once a full FRD token is complete, the next
/// slot reminds that an altitude is required. Parking-name suggestions are unaffected.
/// </summary>
public class AddCommandSuggesterFrdTests
{
    public AddCommandSuggesterFrdTests()
    {
        NavigationDatabase.SetInstance(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["AAAME"] = (37.72, -122.22) })
        );
    }

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
    public void PositionSlot_FullFrd_KnownFix_ShowsBreakdownPreview()
    {
        var suggestions = Suggest("ADD V S P @AAAME093002", []);

        var item = Assert.Single(suggestions, s => s.Text == "@AAAME093002");
        Assert.Contains("AAAME", item.Description);
        Assert.Contains("093", item.Description);
        Assert.Contains("2 nm", item.Description);
        Assert.Contains("altitude", item.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionSlot_FullFrd_UnknownFix_ShowsNotFound()
    {
        var suggestions = Suggest("ADD V S P @ZZZZZ093002", []);

        var item = Assert.Single(suggestions, s => s.Text == "@ZZZZZ093002");
        Assert.Contains("not found", item.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionSlot_RadialOnly_KnownFix_PromptsForDistance()
    {
        var suggestions = Suggest("ADD V S P @AAAME093", []);

        var item = Assert.Single(suggestions, s => s.Text == "@AAAME093");
        Assert.Contains("distance", item.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionSlot_InvalidRadial_KnownFix_ShowsRadialRange()
    {
        var suggestions = Suggest("ADD V S P @AAAME999002", []);

        var item = Assert.Single(suggestions, s => s.Text == "@AAAME999002");
        Assert.Contains("001", item.Description);
        Assert.Contains("360", item.Description);
    }

    [Fact]
    public void PositionSlot_RadialShapedParkingName_UnknownAnchor_NoFrdRow()
    {
        // "GA101" parses as fix "GA" + radial 101, but "GA" isn't a fix — it's a parking spot
        // being typed. Only the parking suggestion should appear.
        var suggestions = Suggest("ADD V S P @GA101", ["GA101"]);

        var item = Assert.Single(suggestions);
        Assert.Equal("@GA101", item.Text);
        Assert.Contains("Parking", item.Description);
    }

    [Fact]
    public void PositionSlot_PlainSpotName_NoFrdRow()
    {
        var suggestions = Suggest("ADD V S P @22", ["22"]);

        var item = Assert.Single(suggestions);
        Assert.Contains("Parking", item.Description);
    }

    [Fact]
    public void AltitudeSlot_AfterFullFrdToken_HintsAltitudeRequired()
    {
        var suggestions = Suggest("ADD V S P @AAAME093002 ", []);

        var item = Assert.Single(suggestions, s => s.Text == "{altitude}");
        Assert.Contains("Required", item.Description);
    }

    [Fact]
    public void AltitudeSlot_AfterParkingToken_NoAltitudeHint()
    {
        var suggestions = Suggest("ADD V S P @H1 ", []);

        Assert.DoesNotContain(suggestions, s => s.Text == "{altitude}");
    }
}

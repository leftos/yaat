using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandInputSuggestionTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    private static AircraftModel Ac(string callsign)
    {
        return new AircraftModel { Callsign = callsign };
    }

    private static CommandInputController Controller()
    {
        return new CommandInputController { NavDbReady = false };
    }

    [Fact]
    public void Follow_TrailingSpace_ShowsAllCallsigns()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456"), Ac("UAL900")];

        controller.UpdateSuggestions("FOLLOW ", "FOLLOW ".Length, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Equal(3, controller.Suggestions.Count);
        Assert.All(controller.Suggestions, s => Assert.Equal(SuggestionKind.Callsign, s.Kind));
        Assert.Contains(controller.Suggestions, s => s.Text == "AAL1234");
        Assert.Contains(controller.Suggestions, s => s.Text == "SWA456");
        Assert.Contains(controller.Suggestions, s => s.Text == "UAL900");
    }

    [Fact]
    public void Follow_PartialArg_FiltersBySubstring()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456"), Ac("UAL900")];

        controller.UpdateSuggestions("FOLLOW AA", "FOLLOW AA".Length, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("AAL1234", controller.Suggestions[0].Text);
        Assert.Equal(SuggestionKind.Callsign, controller.Suggestions[0].Kind);
    }

    [Fact]
    public void Followg_PartialArg_FiltersBySubstring()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("FOLLOWG SW", "FOLLOWG SW".Length, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("SWA456", controller.Suggestions[0].Text);
    }

    [Fact]
    public void Rtis_TrailingSpace_ShowsAllCallsigns()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("RTIS ", "RTIS ".Length, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Equal(2, controller.Suggestions.Count);
        Assert.All(controller.Suggestions, s => Assert.Equal(SuggestionKind.Callsign, s.Kind));
    }

    [Fact]
    public void Rtisf_PartialArg_FiltersBySubstring()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("RTISF AA", "RTISF AA".Length, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("AAL1234", controller.Suggestions[0].Text);
    }

    [Fact]
    public void CvaFollow_TrailingSpace_ShowsCallsigns()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("CVA 28R LEFT FOLLOW ", "CVA 28R LEFT FOLLOW ".Length, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Equal(2, controller.Suggestions.Count);
        Assert.All(controller.Suggestions, s => Assert.Equal(SuggestionKind.Callsign, s.Kind));
    }

    [Fact]
    public void CvaFollow_PartialCallsign_FiltersBySubstring()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("CVA 28R FOLLOW AA", "CVA 28R FOLLOW AA".Length, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("AAL1234", controller.Suggestions[0].Text);
    }

    [Fact]
    public void CvaRunwayPosition_NoCallsignFlyout()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("CVA ", "CVA ".Length, aircraft, Scheme);

        // Runway autocomplete needs NavigationDatabase; without it we just expect no callsign suggestions.
        Assert.DoesNotContain(controller.Suggestions, s => s.Kind == SuggestionKind.Callsign);
    }

    [Fact]
    public void FlyHeading_NoCallsignFlyout()
    {
        // Sanity: non-callsign commands must not spontaneously produce callsign suggestions.
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234")];

        controller.UpdateSuggestions("FH ", "FH ".Length, aircraft, Scheme);

        Assert.DoesNotContain(controller.Suggestions, s => s.Kind == SuggestionKind.Callsign);
    }

    // --- Cursor-aware tests ---

    [Fact]
    public void Caret_OnFirstArg_FiltersByPrefixUpToCursor()
    {
        // "FOLLOW AAL" with caret at 9 (between two A's). Filter by "AA" not "AAL".
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("BBL999")];

        var text = "FOLLOW AAL";
        controller.UpdateSuggestions(text, 9, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("AAL1234", controller.Suggestions[0].Text);
    }

    [Fact]
    public void Caret_OnFirstArg_AcceptingSuggestion_PreservesSuffix()
    {
        // "FOLLOW AA D5L" with caret at 9 (in middle of "AA"). Accept "AAL1234"; suffix "D5L" preserved.
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234")];

        var text = "FOLLOW AA D5L";
        controller.UpdateSuggestions(text, 9, aircraft, Scheme);
        Assert.True(controller.IsSuggestionsVisible);
        controller.SelectedSuggestionIndex = 0;
        var accepted = controller.AcceptSuggestion(text);

        Assert.NotNull(accepted);
        Assert.Equal("FOLLOW AAL1234 D5L", accepted.Value.Text);
        Assert.Equal("FOLLOW AAL1234".Length + 1, accepted.Value.Caret);
    }

    [Fact]
    public void AcceptSuggestion_AtEnd_AppendsTrailingSpace()
    {
        // "FOLLOW AA" with caret at end. Accept "AAL1234" → "FOLLOW AAL1234 ", caret at 15.
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234")];

        var text = "FOLLOW AA";
        controller.UpdateSuggestions(text, text.Length, aircraft, Scheme);
        Assert.True(controller.IsSuggestionsVisible);
        controller.SelectedSuggestionIndex = 0;
        var accepted = controller.AcceptSuggestion(text);

        Assert.NotNull(accepted);
        Assert.Equal("FOLLOW AAL1234 ", accepted.Value.Text);
        Assert.Equal("FOLLOW AAL1234 ".Length, accepted.Value.Caret);
    }

    [Fact]
    public void Caret_InMiddleOfFirstToken_SuggestsCallsignsAndVerbs()
    {
        // "AAL 270" with caret at 1 (inside "AAL"). User editing the callsign position.
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("UAL900")];

        var text = "AAL 270";
        controller.UpdateSuggestions(text, 1, aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        // Should include callsign suggestions matching "A" prefix
        Assert.Contains(controller.Suggestions, s => s.Kind == SuggestionKind.Callsign && s.Text == "AAL1234");
    }
}

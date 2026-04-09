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

        controller.UpdateSuggestions("FOLLOW ", aircraft, Scheme);

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

        controller.UpdateSuggestions("FOLLOW AA", aircraft, Scheme);

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

        controller.UpdateSuggestions("FOLLOWG SW", aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("SWA456", controller.Suggestions[0].Text);
    }

    [Fact]
    public void Rtis_TrailingSpace_ShowsAllCallsigns()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("RTIS ", aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Equal(2, controller.Suggestions.Count);
        Assert.All(controller.Suggestions, s => Assert.Equal(SuggestionKind.Callsign, s.Kind));
    }

    [Fact]
    public void Rtisf_PartialArg_FiltersBySubstring()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("RTISF AA", aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("AAL1234", controller.Suggestions[0].Text);
    }

    [Fact]
    public void CvaFollow_TrailingSpace_ShowsCallsigns()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("CVA 28R LEFT FOLLOW ", aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Equal(2, controller.Suggestions.Count);
        Assert.All(controller.Suggestions, s => Assert.Equal(SuggestionKind.Callsign, s.Kind));
    }

    [Fact]
    public void CvaFollow_PartialCallsign_FiltersBySubstring()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("CVA 28R FOLLOW AA", aircraft, Scheme);

        Assert.True(controller.IsSuggestionsVisible);
        Assert.Single(controller.Suggestions);
        Assert.Equal("AAL1234", controller.Suggestions[0].Text);
    }

    [Fact]
    public void CvaRunwayPosition_NoCallsignFlyout()
    {
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234"), Ac("SWA456")];

        controller.UpdateSuggestions("CVA ", aircraft, Scheme);

        // Runway autocomplete needs NavigationDatabase; without it we just expect no callsign suggestions.
        Assert.DoesNotContain(controller.Suggestions, s => s.Kind == SuggestionKind.Callsign);
    }

    [Fact]
    public void FlyHeading_NoCallsignFlyout()
    {
        // Sanity: non-callsign commands must not spontaneously produce callsign suggestions.
        var controller = Controller();
        IReadOnlyCollection<AircraftModel> aircraft = [Ac("AAL1234")];

        controller.UpdateSuggestions("FH ", aircraft, Scheme);

        Assert.DoesNotContain(controller.Suggestions, s => s.Kind == SuggestionKind.Callsign);
    }
}

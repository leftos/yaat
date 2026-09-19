using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// Autocomplete for the multi-runway CROSS (issue #291): the runway parameter is repeatable, and
/// CROSS carries an HS modifier, so a second CROSS argument slot must keep offering suggestions —
/// including the HS modifier — instead of returning nothing once one runway is typed.
///
/// Also the first-slot half of the modifier gate: a command with a parameterless overload (CROSS, CTO)
/// is a complete command on its own, so its modifiers are meaningful before any argument is typed.
/// </summary>
public class ArgumentSuggesterCrossTests
{
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
            primaryAirportId: null,
            taxiwayNames: [],
            spotNames: [],
            standNames: [],
            maxSuggestions: 20
        );
        return suggestions;
    }

    [Fact]
    public void Cross_EmptyFirstSlot_OffersHoldShortModifier()
    {
        // The bare overload — cross the next hold-short — takes no argument, so HS stands at the first
        // slot: CROSS HS C crosses and then holds short of C.
        ObservableCollection<SuggestionItem> suggestions = Suggest("CROSS ");

        Assert.Contains(suggestions, s => s.Text.Equals("HS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cto_EmptyFirstSlot_OffersModifiers()
    {
        // Bare CTO is a takeoff clearance on its own, so its modifiers apply with nothing else typed.
        ObservableCollection<SuggestionItem> suggestions = Suggest("CTO ");

        Assert.Contains(suggestions, s => s.Text.Equals("IMM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(suggestions, s => s.Text.Equals("WD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cross_SecondArgumentSlot_OffersHoldShortModifier()
    {
        var scheme = CommandScheme.Default();
        // Trailing space → the caret sits on the second CROSS argument slot (parameter index 1).
        const string text = "CROSS 28R ";
        CommandInputParseResult? parsed = CommandInputController.ParseCommandInput(text, text.Length, scheme);
        Assert.NotNull(parsed);
        Assert.Equal(CanonicalCommandType.CrossRunway, parsed.CommandType);
        Assert.Equal(1, parsed.ParameterIndex);

        var suggestions = new ObservableCollection<SuggestionItem>();
        bool added = ArgumentSuggester.TryAddArgumentSuggestions(
            parsed,
            text,
            targetAircraft: null,
            aircraft: [],
            suggestions,
            primaryAirportId: null,
            taxiwayNames: [],
            spotNames: [],
            standNames: [],
            maxSuggestions: 20
        );

        Assert.True(added);
        Assert.Contains(suggestions, s => s.Text.Equals("HS", StringComparison.OrdinalIgnoreCase));
    }
}

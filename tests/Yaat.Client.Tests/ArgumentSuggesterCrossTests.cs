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
/// </summary>
public class ArgumentSuggesterCrossTests
{
    [Fact]
    public void Cross_SecondArgumentSlot_OffersHoldShortModifier()
    {
        var scheme = CommandScheme.Default();
        // Trailing space → the caret sits on the second CROSS argument slot (parameter index 1).
        const string text = "CROSS 28R ";
        var parsed = CommandInputController.ParseCommandInput(text, text.Length, scheme);
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
            maxSuggestions: 20
        );

        Assert.True(added);
        Assert.Contains(suggestions, s => s.Text.Equals("HS", StringComparison.OrdinalIgnoreCase));
    }
}

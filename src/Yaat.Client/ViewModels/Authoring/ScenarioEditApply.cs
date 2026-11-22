using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Writes one edit to the document as one undo step: the General tab's Apply.</summary>
/// <param name="description">The edit's name, as an Undo menu item shows it.</param>
/// <param name="change">Changes the scenario in place.</param>
public delegate void ScenarioEditApply(string description, Action<Scenario> change);

/// <summary>The General tab's id-list text fields: ids separated by commas or whitespace, shown joined with ", ".</summary>
internal static class ScenarioIdListText
{
    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];

    /// <summary>The ids in the text, trimmed, empties dropped, each in its own spelling and in order.</summary>
    /// <param name="text">The typed text.</param>
    /// <returns>The ids.</returns>
    public static List<string> Split(string text) =>
        [.. text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The ids as one text field shows them.</summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The ids joined with ", ".</returns>
    public static string Join(IEnumerable<string> ids) => string.Join(", ", ids);
}

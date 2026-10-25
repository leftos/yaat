using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Pilot;

namespace Yaat.Client.ViewModels;

/// <summary>One answer to "which positions do you work": the lowest level worked, its label and its one-line note.</summary>
/// <param name="Code">The lowest level worked, as a level code (DEL, GND, TWR, APP, CTR).</param>
/// <param name="Label">Every position the student works with this answer, from their own down, e.g. "Ground and Delivery".</param>
/// <param name="Note">What working down to this level means for the calls the student gets.</param>
public sealed record WorkedPositionsChoice(string Code, string Label, string Note);

/// <summary>One radio in the session-setup popup's worked-positions section.</summary>
public sealed partial class WorkedPositionsChoiceRow(WorkedPositionsChoice choice, bool isSelected) : ObservableObject
{
    public string Code { get; } = choice.Code;

    public string Label { get; } = choice.Label;

    public string Note { get; } = choice.Note;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

/// <summary>The worked-positions choices for a solo student: one per level from their own position down to delivery.</summary>
public static class WorkedPositionsChoices
{
    /// <summary>The choices for a student whose own position is <paramref name="ownCode"/>, highest first.</summary>
    /// <param name="ownCode">The student's own level code.</param>
    /// <returns>One choice per level from the student's own down to delivery; empty when the code names no level.</returns>
    public static List<WorkedPositionsChoice> For(string ownCode)
    {
        if (!AtcPositionLevels.TryParse(ownCode, out AtcPositionLevel own))
        {
            return [];
        }

        List<WorkedPositionsChoice> choices = [];
        for (AtcPositionLevel lowest = own; lowest >= AtcPositionLevel.Delivery; lowest--)
        {
            choices.Add(new WorkedPositionsChoice(AtcPositionLevels.Code(lowest), Label(own, lowest), Note(lowest)));
        }

        return choices;
    }

    /// <summary>
    /// Names every level from <paramref name="own"/> down to <paramref name="lowest"/>: "Ground only", "Ground and Delivery",
    /// "Tower, Ground and Delivery".
    /// </summary>
    private static string Label(AtcPositionLevel own, AtcPositionLevel lowest)
    {
        List<string> names = [];
        for (AtcPositionLevel level = own; level >= lowest; level--)
        {
            names.Add(Name(level));
        }

        return names.Count == 1 ? $"{names[0]} only" : $"{string.Join(", ", names[..^1])} and {names[^1]}";
    }

    private static string Name(AtcPositionLevel level) =>
        level switch
        {
            AtcPositionLevel.Delivery => "Delivery",
            AtcPositionLevel.Ground => "Ground",
            AtcPositionLevel.Tower => "Tower",
            AtcPositionLevel.Approach => "Approach",
            AtcPositionLevel.Center => "Center",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown position level"),
        };

    private static string Note(AtcPositionLevel lowest) =>
        lowest switch
        {
            AtcPositionLevel.Delivery => "IFR departures also ask you for their clearance first.",
            AtcPositionLevel.Ground => "Departures call you ready to taxi; no clearance requests.",
            AtcPositionLevel.Tower => "You clear departures and arrivals for the runway; no taxi calls.",
            AtcPositionLevel.Approach => "No tower calls; the runways are worked for you.",
            AtcPositionLevel.Center => "No approach or tower calls.",
            _ => throw new ArgumentOutOfRangeException(nameof(lowest), lowest, "Unknown position level"),
        };
}

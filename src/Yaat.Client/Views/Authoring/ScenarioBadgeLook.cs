using System.Diagnostics;
using Avalonia.Media;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>One badge as the row shows it: an icon drawn in its colour, with a tooltip.</summary>
/// <param name="Icon">The 16 x 16 icon.</param>
/// <param name="Stroke">The icon's line colour, or null for a filled icon.</param>
/// <param name="Fill">The icon's fill, or null for a line icon.</param>
/// <param name="Tip">The tooltip.</param>
public sealed record ScenarioBadgeLook(Geometry Icon, IBrush? Stroke, IBrush? Fill, string Tip)
{
    // An arrow up out of a tray: changes not yet exported.
    private static readonly Geometry ExportArrow = Geometry.Parse("M8,10.5 V2.5 M4.5,6 L8,2.5 L11.5,6 M2.5,10 V13.5 H13.5 V10");

    /// <summary>The look of a badge.</summary>
    /// <param name="badge">The badge.</param>
    /// <param name="problem">The row's problem text, which a corrupt badge's tooltip quotes.</param>
    /// <returns>Its icon, colours and tooltip.</returns>
    public static ScenarioBadgeLook For(ScenarioBadge badge, string? problem) =>
        badge switch
        {
            ScenarioBadge.Modified => new(ScenarioEditorIcons.Dot, null, ScenarioEditorIcons.Amber, "You changed this copy since it was downloaded."),
            ScenarioBadge.ServerChanged => new(
                ScenarioEditorIcons.CloudArrow,
                ScenarioEditorIcons.Blue,
                null,
                "The server version changed since you started this copy."
            ),
            ScenarioBadge.GoneFromServer => new(
                ScenarioEditorIcons.CloudCrossed,
                ScenarioEditorIcons.Grey,
                null,
                "This scenario is no longer on the server. Your downloaded copy is kept."
            ),
            ScenarioBadge.Corrupt => new(ScenarioEditorIcons.Warning, ScenarioEditorIcons.Red, null, CorruptTip(problem)),
            ScenarioBadge.LocalOnly => new(
                ScenarioEditorIcons.Laptop,
                ScenarioEditorIcons.Green,
                null,
                "Exists only on this PC; it is not linked to a server scenario."
            ),
            ScenarioBadge.Unexported => new(ExportArrow, ScenarioEditorIcons.Amber, null, "Has changes not yet exported as JSON."),
            _ => throw new UnreachableException($"No look for the badge {badge}."),
        };

    private static string CorruptTip(string? problem) =>
        string.IsNullOrWhiteSpace(problem)
            ? "This file cannot be read. It is kept as is."
            : $"This file cannot be read: {problem.Trim().TrimEnd('.')}. It is kept as is.";
}

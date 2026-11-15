namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>Identity of one Scope toolbar button, top-level or in a menu.</summary>
public enum ScopeButtonId
{
    /// <summary>The Template menu.</summary>
    Template,

    /// <summary>The Template menu's STARS pick.</summary>
    TemplateStars,

    /// <summary>The Template menu's ERAM pick.</summary>
    TemplateEram,

    /// <summary>The Template menu's Mixed pick.</summary>
    TemplateMixed,

    /// <summary>The Template menu's EuroScope pick.</summary>
    TemplateEuroScope,

    /// <summary>The range spinner.</summary>
    Range,
}

/// <summary>How a Scope toolbar button acts.</summary>
public enum ScopeButtonKind
{
    /// <summary>A click runs the button's command.</summary>
    Command,

    /// <summary>A click turns the button's setting on or off.</summary>
    Toggle,

    /// <summary>A click opens or closes the tray of the button's children.</summary>
    Menu,

    /// <summary>The wheel steps the button's value; it shows the value after its label.</summary>
    Spinner,
}

/// <summary>One Scope toolbar button.</summary>
/// <param name="Id">The button's identity.</param>
/// <param name="Label">The uppercase label the button shows.</param>
/// <param name="Kind">How the button acts.</param>
/// <param name="Children">A menu's buttons, in tray order; empty for any other kind.</param>
/// <param name="Value">The value a command button picks (a template id), or null.</param>
public sealed record ScopeButton(ScopeButtonId Id, string Label, ScopeButtonKind Kind, IReadOnlyList<ScopeButton> Children, string? Value);

/// <summary>The Scope toolbar's buttons, in bar order, and the <c>.scope</c> commands they run.</summary>
public static class ScopeToolbarButtons
{
    /// <summary>The bar's top-level buttons, left to right. A button is listed only once its behaviour exists.</summary>
    public static readonly IReadOnlyList<ScopeButton> All =
    [
        new(
            ScopeButtonId.Template,
            "TEMPLATE",
            ScopeButtonKind.Menu,
            [
                TemplatePick(ScopeButtonId.TemplateStars, "STARS", ScopeTemplates.StarsId),
                TemplatePick(ScopeButtonId.TemplateEram, "ERAM", ScopeTemplates.EramId),
                TemplatePick(ScopeButtonId.TemplateMixed, "MIXED", ScopeTemplates.MixedId),
                TemplatePick(ScopeButtonId.TemplateEuroScope, "EUROSCOPE", ScopeTemplates.EuroScopeId),
            ],
            null
        ),
        new(ScopeButtonId.Range, "RANGE", ScopeButtonKind.Spinner, [], null),
    ];

    /// <summary>The command that picks the template (or Mixed) with <paramref name="templateId"/>.</summary>
    public static string TemplateCommand(string templateId) => $".scope template {templateId}";

    /// <summary>The command that sets the scope's range to <paramref name="rangeNm"/> NM.</summary>
    public static string RangeCommand(int rangeNm) => $".scope range {rangeNm}";

    private static ScopeButton TemplatePick(ScopeButtonId id, string label, string templateId) =>
        new(id, label, ScopeButtonKind.Command, [], templateId);
}

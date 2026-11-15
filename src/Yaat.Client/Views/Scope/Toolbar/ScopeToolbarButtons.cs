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

    /// <summary>The Views menu.</summary>
    Views,

    /// <summary>The Views menu's SSA toggle.</summary>
    ViewSsa,

    /// <summary>The Views menu's LA/CA/MCI toggle.</summary>
    ViewCa,
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
/// <param name="Value">
/// The value the button stands for: the template id a template pick picks, the view name a view toggle opens; else null.
/// </param>
/// <param name="Command">
/// A command button's full dot command; a toggle's command stem, to which a press appends <c> on</c> or <c> off</c>; null for
/// a menu or a spinner.
/// </param>
public sealed record ScopeButton(
    ScopeButtonId Id,
    string Label,
    ScopeButtonKind Kind,
    IReadOnlyList<ScopeButton> Children,
    string? Value,
    string? Command
);

/// <summary>The Scope toolbar's buttons, in bar order, and the <c>.scope</c> and <c>.view</c> commands they run.</summary>
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
            null,
            null
        ),
        new(ScopeButtonId.Range, "RANGE", ScopeButtonKind.Spinner, [], null, null),
        new(
            ScopeButtonId.Views,
            "VIEWS",
            ScopeButtonKind.Menu,
            [ViewToggle(ScopeButtonId.ViewSsa, "SSA", ViewSsaName), ViewToggle(ScopeButtonId.ViewCa, "LA/CA/MCI", ViewCaName)],
            null,
            null
        ),
    ];

    /// <summary>The SSA's view name, as <c>.view</c> takes it.</summary>
    public const string ViewSsaName = "ssa";

    /// <summary>The LA/CA/MCI list's view name, as <c>.view</c> takes it.</summary>
    public const string ViewCaName = "ca";

    /// <summary>The command that opens (<paramref name="open"/>) or closes the view named <paramref name="viewName"/>.</summary>
    public static string ViewCommand(string viewName, bool open) => $"{ViewCommandStem(viewName)} {(open ? "on" : "off")}";

    /// <summary>The command that picks the template (or Mixed) with <paramref name="templateId"/>.</summary>
    public static string TemplateCommand(string templateId) => $".scope template {templateId}";

    /// <summary>The command that sets the scope's range to <paramref name="rangeNm"/> NM.</summary>
    public static string RangeCommand(int rangeNm) => $".scope range {rangeNm}";

    private static ScopeButton TemplatePick(ScopeButtonId id, string label, string templateId) =>
        new(id, label, ScopeButtonKind.Command, [], templateId, TemplateCommand(templateId));

    private static ScopeButton ViewToggle(ScopeButtonId id, string label, string viewName) =>
        new(id, label, ScopeButtonKind.Toggle, [], viewName, ViewCommandStem(viewName));

    private static string ViewCommandStem(string viewName) => $".view {viewName}";
}

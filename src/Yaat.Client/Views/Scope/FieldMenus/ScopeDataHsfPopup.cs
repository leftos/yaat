using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>Which ERAM fourth-line popup <see cref="ScopeDataHsfPopup"/> is.</summary>
internal enum HsfPopupKind
{
    /// <summary>The heading section beside the PILOT Fly heading panel.</summary>
    Heading,

    /// <summary>The speed section beside the PILOT Assign speed panel.</summary>
    Speed,

    /// <summary>The free-text section alone, with no PILOT column.</summary>
    FreeText,

    /// <summary>The heading and speed sections beside Fly heading above Assign speed.</summary>
    Combined,
}

/// <summary>
/// The popup a click on an ERAM block's fourth line opens below the block (<see cref="HsfPopupKind"/>): SCOPE DATA sections
/// for the HSF heading, speed or free text, each a caption with the stored value, a box opening with that value selected, and
/// rows showing the ERAM entry they run (<see cref="ScopeDataEntries"/>), beside the PILOT panels the catalog's Fly heading and
/// Assign speed entries build. A Set row is enabled while its box holds a value the server takes
/// (<see cref="EramEntryEngine.ParseHsfHeading"/>, <see cref="EramEntryEngine.ParseHsfSpeed"/>, one to eight free-text
/// characters); a Clear row while its value is stored. The free-text box takes only what QS free text accepts, upper-cased.
/// </summary>
internal sealed class ScopeDataHsfPopup : ScopeDataPopupBase
{
    /// <summary>The heading section's caption.</summary>
    public const string HeadingCaption = "HEADING";

    /// <summary>The speed section's caption.</summary>
    public const string SpeedCaption = "SPEED";

    /// <summary>The free-text section's caption.</summary>
    public const string FreeTextCaption = "FREE TEXT";

    /// <summary>What the empty heading box reads.</summary>
    public const string HeadingPlaceholder = "e.g. H300 or 20L";

    /// <summary>What the empty speed box reads.</summary>
    public const string SpeedPlaceholder = "e.g. S230, 250-, M78";

    /// <summary>What the empty free-text box reads.</summary>
    public const string FreeTextPlaceholder = "e.g. LOWALT";

    /// <summary>The most characters HSF free text holds.</summary>
    public const int FreeTextMaxLength = 8;

    private readonly AircraftModel _aircraft;
    private readonly TextBox _focusBox;

    /// <param name="aircraft">The clicked track, whose callsign every entry names last and whose HSF values the boxes open with.</param>
    /// <param name="host">The host the PILOT panels send through and follow the clock of.</param>
    /// <param name="hooks">Shift, the SCOPE DATA pick, the column each send came from, and whether the popup is tabbed.</param>
    /// <param name="kind">Which sections and PILOT panels the popup holds.</param>
    public ScopeDataHsfPopup(AircraftModel aircraft, IMenuHost host, ScopeDataPopupHooks hooks, HsfPopupKind kind)
        : base(aircraft.Callsign, hooks)
    {
        _aircraft = aircraft;
        Kind = kind;
        HsfSections sections = BuildSections(kind, Chooser(host));
        HeadingBox = sections.HeadingBox;
        SpeedBox = sections.SpeedBox;
        FreeTextBox = sections.FreeTextBox;
        HeadingPanel = sections.HeadingPanel;
        SpeedPanel = sections.SpeedPanel;
        _focusBox = sections.FirstBox(kind);

        StackPanel column = SectionsColumn();
        Control[] pilot = sections.PilotPanels();
        Control content =
            (pilot.Length == 0) ? SingleColumnPanel(column) : Panel(column, PilotColumn(pilot), scrollPilot: kind == HsfPopupKind.Combined);
        Attach(content, hooks.RecordShift, host, [.. pilot.OfType<IFlyoutPanel>()]);
    }

    // The boxes and PILOT panels of a kind: heading for Heading and Combined, speed for Speed and Combined, free text alone.
    private sealed record HsfSections(
        TextBox? HeadingBox,
        TextBox? SpeedBox,
        TextBox? FreeTextBox,
        HeadingFlyoutPanel? HeadingPanel,
        SpeedFlyoutPanel? SpeedPanel
    )
    {
        // The first section's box, which takes the focus as the popup opens.
        public TextBox FirstBox(HsfPopupKind kind) =>
            HeadingBox ?? SpeedBox ?? FreeTextBox ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No section.");

        // Fly heading above Assign speed, each only where its section is.
        public Control[] PilotPanels()
        {
            Control?[] panels = [HeadingPanel, SpeedPanel];
            return [.. panels.OfType<Control>()];
        }
    }

    private HsfSections BuildSections(HsfPopupKind kind, Func<string, Task> choose)
    {
        (TextBox Box, HeadingFlyoutPanel Panel)? heading = (kind is HsfPopupKind.Heading or HsfPopupKind.Combined) ? HeadingParts(choose) : null;
        (TextBox Box, SpeedFlyoutPanel Panel)? speed = (kind is HsfPopupKind.Speed or HsfPopupKind.Combined) ? SpeedParts(choose) : null;
        TextBox? freeText = (kind == HsfPopupKind.FreeText) ? NewFreeTextBox(_aircraft.EramFreeText) : null;
        return new HsfSections(heading?.Box, speed?.Box, freeText, heading?.Panel, speed?.Panel);
    }

    // The heading section's box and its PILOT Fly heading panel.
    private (TextBox Box, HeadingFlyoutPanel Panel) HeadingParts(Func<string, Task> choose) =>
        (ValueBox(HeadingPlaceholder, _aircraft.EramAssignedHeading), HeadingFlyout.FlyPanelFactory(_aircraft)(choose));

    // The speed section's box and its PILOT Assign speed panel.
    private (TextBox Box, SpeedFlyoutPanel Panel) SpeedParts(Func<string, Task> choose) =>
        (ValueBox(SpeedPlaceholder, _aircraft.EramAssignedSpeed), SpeedFlyout.AssignPanelFactory(_aircraft)(choose, Close));

    /// <summary>Which popup this is.</summary>
    public HsfPopupKind Kind { get; }

    /// <summary>The SCOPE DATA heading box; null without a heading section.</summary>
    public TextBox? HeadingBox { get; }

    /// <summary>The SCOPE DATA speed box, the speed without the slash; null without a speed section.</summary>
    public TextBox? SpeedBox { get; }

    /// <summary>The SCOPE DATA free-text box; null without a free-text section.</summary>
    public TextBox? FreeTextBox { get; }

    /// <summary>The PILOT Fly heading panel; null without a heading section.</summary>
    public HeadingFlyoutPanel? HeadingPanel { get; }

    /// <summary>The PILOT Assign speed panel; null without a speed section.</summary>
    public SpeedFlyoutPanel? SpeedPanel { get; }

    /// <inheritdoc/>
    protected override TextBox FocusBox => _focusBox;

    // The box opens with its stored value selected, so typing replaces it.
    protected override void AfterFocus() => _focusBox.SelectAll();

    // The PILOT tab focuses the Fly heading panel's box, or the Assign speed panel's.
    protected override Control? PilotFocus => (Control?)HeadingPanel?.Box ?? SpeedPanel?.Box;

    // What the free-text box keeps of what it is given: the characters QS free text accepts, upper-cased, the first eight.
    private static string FreeTextAccepted(string typed) =>
        new([.. typed.ToUpperInvariant().Where(EramEntryEngine.IsQsFreeTextCharacter).Take(FreeTextMaxLength)]);

    private static string NowNote(string? stored) => IsStored(stored) ? $"now {stored}" : "none";

    private static bool IsStored(string? value) => value is { Length: > 0 };

    private TextBox ValueBox(string placeholder, string? value)
    {
        TextBox box = NewBox(placeholder);
        box.Text = value ?? "";
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Present();
            }
        };
        return box;
    }

    // The free-text box: whatever it is given becomes its accepted characters, which the rows then show.
    private TextBox NewFreeTextBox(string? value)
    {
        TextBox box = NewBox(FreeTextPlaceholder);
        box.Text = FreeTextAccepted(value ?? "");
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty)
            {
                return;
            }

            string typed = box.Text ?? "";
            string accepted = FreeTextAccepted(typed);
            if (accepted != typed)
            {
                // The caret stays where it was, less the characters dropped.
                int caret = box.CaretIndex - (typed.Length - accepted.Length);
                box.Text = accepted;
                box.CaretIndex = Math.Clamp(caret, 0, accepted.Length);
                return;
            }

            Present();
        };
        return box;
    }

    private string? HeadingEntry()
    {
        string text = (HeadingBox?.Text ?? "").Trim();
        return (EramEntryEngine.ParseHsfHeading(text) is null) ? null : ScopeDataEntries.HsfHeading(text, Flid);
    }

    // The speed as typed, a leading slash dropped, since the entry puts its own before it.
    private string? SpeedEntry()
    {
        string text = (SpeedBox?.Text ?? "").Trim();
        text = text.StartsWith('/') ? text[1..] : text;
        return ((text.Length == 0) || (EramEntryEngine.ParseHsfSpeed(text) is null)) ? null : ScopeDataEntries.HsfSpeed(text, Flid);
    }

    private string? FreeTextEntry()
    {
        string text = FreeTextBox?.Text ?? "";
        return ((text.Length is >= 1 and <= FreeTextMaxLength) && text.All(EramEntryEngine.IsQsFreeTextCharacter))
            ? ScopeDataEntries.HsfFreeText(text, Flid)
            : null;
    }

    private bool AnyStored() => IsStored(_aircraft.EramAssignedHeading) || IsStored(_aircraft.EramAssignedSpeed) || IsStored(_aircraft.EramFreeText);

    private StackPanel SectionsColumn()
    {
        StackPanel column = ScopeDataColumn();
        if (HeadingBox is { } heading)
        {
            AddSection(column, HeadingCaption, () => _aircraft.EramAssignedHeading, heading);
            AddRow(column, "Set heading", "", HeadingEntry, ScopeDataEntries.HsfHeading(Elided, Flid));
            AddClearRow(column, "Clear heading", "", () => IsStored(_aircraft.EramAssignedHeading), ScopeDataEntries.HsfClearHeading(Flid));
        }

        if (SpeedBox is { } speed)
        {
            if (HeadingBox is not null)
            {
                column.Children.Add(SectionRule());
            }

            AddSection(column, SpeedCaption, () => _aircraft.EramAssignedSpeed, speed);
            AddRow(column, "Set speed", "", SpeedEntry, ScopeDataEntries.HsfSpeed(Elided, Flid));
            AddClearRow(column, "Clear speed", "", () => IsStored(_aircraft.EramAssignedSpeed), ScopeDataEntries.HsfClearSpeed(Flid));
        }

        if (FreeTextBox is { } freeText)
        {
            AddSection(column, FreeTextCaption, () => _aircraft.EramFreeText, freeText);
            AddRow(column, "Set free text", "", FreeTextEntry, ScopeDataEntries.HsfFreeText(Elided, Flid));
            AddClearRow(column, "Clear all HSF", "heading, speed, text", AnyStored, ScopeDataEntries.HsfClearAll(Flid));
        }

        return column;
    }

    // A section's caption with its stored value, read again with the rows, its box, and the line above its rows.
    private void AddSection(StackPanel column, string caption, Func<string?> stored, TextBox box)
    {
        AddSectionCaption(column, caption, () => NowNote(stored()));
        column.Children.Add(BoxRow(box));
        column.Children.Add(FlyoutPanelParts.SeparatorLine());
    }
}

using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Client.ContextMenus;

/// <summary>Which box the Report when… flyout focuses as it opens.</summary>
internal enum ReportWhenFocus
{
    /// <summary>The miles box, or the fix box when the aircraft has no runway and so no Mile final row.</summary>
    MilesBox,

    /// <summary>The fix box, which every aircraft has.</summary>
    FixBox,
}

/// <summary>
/// The Report when… flyout, Report when… and its two aliases (At N-mile final…, At fix…) alike. While a report is armed
/// (<see cref="IMenuAircraft.ArmedReports"/>) it opens with the Stop reporting section: one row per armed report sending its
/// single cancel, then All reports. Then the title line naming the aircraft and its runway; in a pattern circuit
/// (<see cref="AircraftCommandApplicability.IsInPatternCircuit"/>) the five turn rows, an armed one badged; with an assigned
/// runway the Mile final row, chips 2, 3, 5 and 10 sending at once beside a checked miles box; the Passing a fix box, checked
/// against the navigation data; then the reporting points: an IFR aircraft's route fixes in order, every other aircraft's
/// nearby reporting points nearest first, each with its distance and magnetic bearing and sending at once. Rows the sim would
/// refuse are left out. Enter in a box sends its report; a click on a box row only focuses its box.
/// </summary>
internal static class ReportWhenFlyout
{
    /// <summary>The title line's title.</summary>
    public const string Title = "Report when…";

    /// <summary>The miles box's name.</summary>
    public const string MilesBoxName = "ReportMilesBox";

    /// <summary>The fix box's name.</summary>
    public const string FixBoxName = "ReportFixBox";

    /// <summary>The reason the miles box gives for a decimal distance.</summary>
    public const string WholeMilesOnly = "Whole miles only";

    /// <summary>The reason the miles box gives for text that is no number, or a number under 1.</summary>
    public const string OneMileOrMore = "1 mile or more";

    /// <summary>The reason the fix box gives for text the sim does not read as a report fix's name.</summary>
    public const string NotAFixName = "Not a fix name";

    /// <summary>The reason the fix box gives for a fix name the navigation data does not know.</summary>
    public const string UnknownFix = "Not a known fix";

    /// <summary>The Stop section's group line.</summary>
    public const string StopGroup = "Stop reporting";

    /// <summary>The turn rows' group line.</summary>
    public const string TurningGroup = "Turning · repeats each circuit until stopped";

    /// <summary>The nearby reporting points' group line.</summary>
    public const string NearbyGroup = "Reporting points · nearest first";

    /// <summary>The route fixes' group line.</summary>
    public const string RouteGroup = "Route fixes · in order";

    /// <summary>The style class an armed mile chip carries.</summary>
    public const string ArmedClass = "armed";

    /// <summary>The teal of the armed badge and an armed chip's edge.</summary>
    public static IImmutableSolidColorBrush ArmedBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#6EE0CE"));

    /// <summary>The mile final chips, in miles.</summary>
    private static readonly int[] MileChips = [2, 3, 5, 10];

    /// <summary>The most route fixes the flyout lists.</summary>
    private const int MaxRouteFixes = 6;

    /// <summary>The turn reports in circuit order: their leg, their row's label and the word the command names them by.</summary>
    private static readonly (ArmedReportLegs Leg, string Label, string Word)[] Legs =
    [
        (ArmedReportLegs.Crosswind, "Turning crosswind", "CROSSWIND"),
        (ArmedReportLegs.Downwind, "Turning downwind", "DOWNWIND"),
        (ArmedReportLegs.Midfield, "Midfield downwind", "MIDFIELD"),
        (ArmedReportLegs.Base, "Turning base", "BASE"),
        (ArmedReportLegs.Final, "Turning final", "FINAL"),
    ];

    /// <summary>
    /// The Report when… flyout headed <paramref name="header"/> for <paramref name="aircraft"/>, holding the focus in the box
    /// <paramref name="focus"/> names as it opens; its strip button carries the armed dot while a report is armed.
    /// </summary>
    public static MenuItem Build(string header, IMenuAircraft? aircraft, ReportWhenFocus focus, MenuContext context, IMenuHost host)
    {
        ArmedReportsDto? armed = aircraft?.ArmedReports;
        var menu = new MenuItem { Header = header };
        if (armed is not null)
        {
            AddStopSection(menu.Items, armed, context, host);
            QuickCommandStrip.MarkArmed(menu);
        }

        menu.Items.Add(TitleItem(aircraft, context));
        if ((aircraft is not null) && AircraftCommandApplicability.IsInPatternCircuit(aircraft))
        {
            AddTurnRows(menu.Items, armed, context, host);
        }

        Task Send(string command) => host.SendAsync(context.Callsign, command, context.Initials);
        InlineBoxRow? milesRow = string.IsNullOrEmpty(aircraft?.AssignedRunway) ? null : MilesRow(armed?.FinalMiles, Send);
        if (milesRow is not null)
        {
            menu.Items.Add(milesRow.Item);
        }

        InlineBoxRow fixRow = FixRow(armed?.Fix, host, Send);
        menu.Items.Add(fixRow.Item);
        if (aircraft is not null)
        {
            AddPointRows(menu.Items, aircraft, armed?.Fix, context, host);
        }

        SharedMenuGroups.FocusTheBoxOnOpen(menu, ((focus == ReportWhenFocus.MilesBox) && (milesRow is not null)) ? milesRow : fixRow);
        return menu;
    }

    /// <summary>The Stop section: its group line, a row per armed report sending its single cancel, All reports, a separator.</summary>
    private static void AddStopSection(ItemCollection items, ArmedReportsDto armed, MenuContext context, IMenuHost host)
    {
        items.Add(SharedMenuGroups.DetailRow(StopGroup));
        foreach ((ArmedReportLegs leg, string label, string word) in Legs)
        {
            if (armed.Legs.HasFlag(leg))
            {
                items.Add(SendItem(new ReportWhenRow(label, false, null, $"REPORT OFF {word}"), context, host));
            }
        }

        if (armed.FinalMiles is { } miles)
        {
            items.Add(SendItem(new ReportWhenRow($"{miles}-mile final", false, null, $"REPORT OFF {miles} FINAL"), context, host));
        }

        if (armed.Fix is { } fix)
        {
            items.Add(SendItem(new ReportWhenRow($"Passing {fix}", false, null, $"REPORT OFF {fix}"), context, host));
        }

        items.Add(SendItem(new ReportWhenRow("All reports", false, null, "REPORT OFF"), context, host));
        items.Add(new Separator());
    }

    /// <summary>The turn rows under their group line, an armed one badged, then a separator.</summary>
    private static void AddTurnRows(ItemCollection items, ArmedReportsDto? armed, MenuContext context, IMenuHost host)
    {
        items.Add(SharedMenuGroups.DetailRow(TurningGroup));
        foreach ((ArmedReportLegs leg, string label, string word) in Legs)
        {
            bool isArmed = (armed is not null) && armed.Legs.HasFlag(leg);
            items.Add(SendItem(new ReportWhenRow(label, isArmed, null, $"REPORT {word}"), context, host));
        }

        items.Add(new Separator());
    }

    /// <summary>
    /// The reporting points under their group line, the armed fix's row badged: an IFR aircraft's route fixes
    /// (<see cref="RoutePoints"/>), else the host's nearby reporting points; nothing, not even the group line, when there are none.
    /// </summary>
    private static void AddPointRows(ItemCollection items, IMenuAircraft aircraft, string? armedFix, MenuContext context, IMenuHost host)
    {
        bool ifr = string.Equals(aircraft.FlightRules, "IFR", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<NearbyReportingPoint> points = ifr ? RoutePoints(aircraft, host) : host.GetReportingPointsNearby(aircraft.Position);
        if (points.Count == 0)
        {
            return;
        }

        items.Add(SharedMenuGroups.DetailRow(ifr ? RouteGroup : NearbyGroup));
        foreach (NearbyReportingPoint point in points)
        {
            bool isArmed = string.Equals(point.Name, armedFix, StringComparison.OrdinalIgnoreCase);
            var row = new ReportWhenRow(point.Name, isArmed, PointNote(aircraft, point), $"REPORT {point.Name}");
            items.Add(SendItem(row, context, host));
        }
    }

    /// <summary>
    /// The route's fixes from the one the aircraft is navigating to on, in order, each placed by the host, leaving out a fix the
    /// sim reads as no report fix name or the host does not place; at most <see cref="MaxRouteFixes"/>.
    /// </summary>
    private static List<NearbyReportingPoint> RoutePoints(IMenuAircraft aircraft, IMenuHost host)
    {
        IEnumerable<string> ahead = aircraft.NavigationRoute.Contains(aircraft.NavigatingTo, StringComparer.Ordinal)
            ? aircraft.NavigationRoute.SkipWhile(fix => !string.Equals(fix, aircraft.NavigatingTo, StringComparison.Ordinal))
            : aircraft.NavigationRoute;
        List<NearbyReportingPoint> points = [];
        foreach (string fix in ahead)
        {
            if (points.Count == MaxRouteFixes)
            {
                break;
            }

            if (CommandParser.IsReportFixName(fix) && (host.GetFixPosition(fix) is { } position))
            {
                points.Add(
                    new NearbyReportingPoint(fix, GeoMath.DistanceNm(aircraft.Position, position), GeoMath.BearingTo(aircraft.Position, position))
                );
            }
        }

        return points;
    }

    /// <summary>
    /// A point's note: its distance, one decimal, and its bearing from the aircraft, magnetic at the aircraft's position, whole
    /// degrees from 001 to 360: <c>0.8 NM · 347°</c>.
    /// </summary>
    internal static string PointNote(IMenuAircraft aircraft, NearbyReportingPoint point)
    {
        int bearing = HeadingFlyout.WholeHeading(MagneticDeclination.TrueToMagnetic(point.BearingTrueDeg, aircraft.Position));
        return string.Create(CultureInfo.InvariantCulture, $"{point.DistanceNm:0.0} NM · {bearing:000}°");
    }

    /// <summary>A send row's menu item, sending <see cref="ReportWhenRow.Command"/> at once.</summary>
    private static MenuItem SendItem(ReportWhenRow row, MenuContext context, IMenuHost host) =>
        MenuCatalog.BuildTemplatedSend(row, ReportWhenRowTemplate.Instance, row.Command, context, host);

    /// <summary>The disabled title line: <c>Report when…</c>, then the callsign and the runway, when the aircraft has one.</summary>
    private static MenuItem TitleItem(IMenuAircraft? aircraft, MenuContext context)
    {
        string runway = aircraft?.AssignedRunway ?? "";
        var title = new ReportWhenTitleLine(string.IsNullOrEmpty(runway) ? context.Callsign : $"{context.Callsign} · RWY {runway}");
        var item = new MenuItem { Header = title, IsEnabled = false };
        AutomationProperties.SetName(item, title.ToString());
        return item;
    }

    /// <summary>
    /// The Mile final row: the chips beside the miles box, the armed chip marked; its label notes an armed mile final no chip
    /// shows, else that a new one replaces the armed one.
    /// </summary>
    private static InlineBoxRow MilesRow(int? armedMiles, Func<string, Task> send)
    {
        string label = armedMiles switch
        {
            null => "Mile final",
            { } miles when MileChips.Contains(miles) => $"Mile final · replaces the armed {miles}-mile final",
            { } miles => $"Mile final · armed: {miles}-mile final",
        };
        InlineBoxSpec spec = BoxSpec(label, MenuIds.ApproachReportNMileFinal, "miles", MilesBoxName, 60, null, text => ReadMiles(text, armedMiles));
        var row = InlineBoxRow.CreateFocusingOnClick(spec, send);
        row.PutBesideBox([.. MileChips.Select(miles => MileChip(miles, miles == armedMiles, row, send))]);
        return row;
    }

    /// <summary>The Passing a fix row: the fix box, suggesting fixes; its label notes that a new fix report replaces the armed one.</summary>
    private static InlineBoxRow FixRow(string? armedFix, IMenuHost host, Func<string, Task> send)
    {
        string label = (armedFix is null) ? "Passing a fix" : $"Passing a fix · replaces passing {armedFix}";
        InlineBoxSpec spec = BoxSpec(label, MenuIds.ApproachReportAtFix, "fix name", FixBoxName, 160, host.SuggestFixes, text => ReadFix(text, host));
        return InlineBoxRow.CreateFocusingOnClick(spec, send);
    }

    private static InlineBoxSpec BoxSpec(
        string label,
        string glyphId,
        string placeholder,
        string boxName,
        double width,
        Func<string, IReadOnlyList<string>>? suggestions,
        Func<string, InlineBoxReading> read
    ) =>
        new()
        {
            Label = label,
            Glyph = QuickCommandGlyphs.For(glyphId),
            BlankGlyphSlot = true,
            Verb = "",
            Placeholder = placeholder,
            BoxName = boxName,
            BoxWidth = width,
            Seed = "",
            FixSuggestions = suggestions,
            Read = read,
        };

    /// <summary>
    /// A mile chip, <c>5</c>, sending <c>REPORT 5 FINAL</c> at once and closing the menu, as Enter in the miles box does; the armed
    /// mile final's chip carries <see cref="ArmedClass"/> and the armed edge.
    /// </summary>
    private static Button MileChip(int miles, bool isArmed, InlineBoxRow row, Func<string, Task> send)
    {
        string command = $"REPORT {miles} FINAL";
        Button chip = FlyoutPanelParts.Chip(
            miles.ToString(CultureInfo.InvariantCulture),
            command,
            $"{miles}-mile final — {command}",
            async chosen =>
            {
                Task sending = send(chosen);
                row.Item.FindLogicalAncestorOfType<ContextMenu>()?.Close();
                await sending;
            }
        );
        chip.Width = 30;
        chip.Margin = new Thickness(0, 3, 0, 0);
        chip.VerticalAlignment = VerticalAlignment.Top;
        if (isArmed)
        {
            chip.Classes.Add(ArmedClass);
            chip.BorderBrush = ArmedBrush;
        }

        return chip;
    }

    /// <summary>
    /// The miles box's reading: whole miles, 1 or more, read back as the pilot's answer (and, with a mile final armed, that it
    /// replaces it, unless the miles typed are the armed ones); a decimal refused as whole miles only; anything else refused as
    /// 1 mile or more; blank waits.
    /// </summary>
    private static InlineBoxReading ReadMiles(string text, int? armedMiles)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return InlineBoxReading.Blank;
        }

        if (trimmed.Contains('.') && double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
        {
            return InlineBoxReading.Refuses(WholeMilesOnly);
        }

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int miles) || (miles < 1))
        {
            return InlineBoxReading.Refuses(OneMileOrMore);
        }

        string replaces = ((armedMiles is { } armed) && (armed != miles)) ? $" · replaces the armed {armed}-mile final" : "";
        return InlineBoxReading.Reads($"REPORT {miles} FINAL", $"Pilot: will report {miles}-mile final{replaces}");
    }

    /// <summary>
    /// The fix box's reading: a name the sim reads as a report fix (<see cref="CommandParser.IsReportFixName"/>) that the host
    /// places, upper-cased and read back as the pilot's answer. Blank waits, and so does a partial name without digits that the
    /// fix suggestions still offer names for (<see cref="IMenuHost.SuggestFixes"/>), as Warp's FRD box does; any other name is
    /// refused.
    /// </summary>
    private static InlineBoxReading ReadFix(string text, IMenuHost host)
    {
        string fix = text.Trim().ToUpperInvariant();
        if (fix.Length == 0)
        {
            return InlineBoxReading.Blank;
        }

        bool reportFix = CommandParser.IsReportFixName(fix);
        if (reportFix && (host.GetFixPosition(fix) is not null))
        {
            return InlineBoxReading.Reads($"REPORT {fix}", $"Pilot: will report passing {fix}");
        }

        if (!fix.Any(char.IsAsciiDigit) && (host.SuggestFixes(fix).Count > 0))
        {
            return InlineBoxReading.Blank;
        }

        return InlineBoxReading.Refuses(reportFix ? UnknownFix : NotAFixName);
    }
}

/// <summary>
/// A Report when… send row: a turn, Stop or reporting-point row, drawn by <see cref="ReportWhenRowTemplate"/>.
/// </summary>
/// <param name="Label">What the row names: <c>Turning base</c>, <c>VPCOL</c>.</param>
/// <param name="IsArmed">Whether the report the row arms is armed now, which the row's badge shows.</param>
/// <param name="Note">The note after the label, a point's distance and bearing; null for none.</param>
/// <param name="Command">The command the row sends.</param>
internal sealed record ReportWhenRow(string Label, bool IsArmed, string? Note, string Command)
{
    /// <summary>The badge an armed row carries.</summary>
    public const string ArmedBadge = "armed";

    /// <summary>The row as one line, <c>Label [armed] · Note — Command</c>, the badge and note left out when there are none.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(Label);
        if (IsArmed)
        {
            text.Append(" [").Append(ArmedBadge).Append(']');
        }

        if (Note is { } note)
        {
            text.Append(" · ").Append(note);
        }

        return text.Append(" — ").Append(Command).ToString();
    }
}

/// <summary>
/// The view of a <see cref="ReportWhenRow"/>: a blank glyph slot, so the label lines up with the box rows' labels, the label, the
/// teal armed badge, the dimmed note, then the command right-aligned in the dimmed monospace font.
/// </summary>
internal sealed class ReportWhenRowTemplate : FuncDataTemplate<ReportWhenRow>
{
    /// <summary>The one template every Report when… send row uses.</summary>
    public static ReportWhenRowTemplate Instance { get; } = new();

    private ReportWhenRowTemplate()
        : base((row, _) => Build(row)) { }

    private static Grid Build(ReportWhenRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Control slot = QuickCommandStrip.GlyphSlot();
        slot.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(slot);

        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center });
        if (row.IsArmed)
        {
            label.Children.Add(Badge());
        }

        if (row.Note is { } note)
        {
            label.Children.Add(
                new TextBlock
                {
                    Text = $" · {note}",
                    FontSize = 12,
                    Opacity = 0.8,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        TextBlock command = FlyoutPanelParts.CommandText(row.Command);
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        return grid;
    }

    private static Border Badge() =>
        new()
        {
            BorderBrush = ReportWhenFlyout.ArmedBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = ReportWhenRow.ArmedBadge,
                FontSize = 10,
                Foreground = ReportWhenFlyout.ArmedBrush,
            },
        };
}

/// <summary>The Report when… flyout's title line: <c>Report when…</c>, then <see cref="Right"/> right, in the monospace font.</summary>
internal sealed class ReportWhenTitleLine : Decorator
{
    /// <summary>A title line reading <paramref name="right"/> after the title.</summary>
    public ReportWhenTitleLine(string right)
    {
        Right = right;
        Grid line = FlyoutPanelParts.TitleLine(
            new TextBlock
            {
                Text = ReportWhenFlyout.Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            },
            new TextBlock
            {
                Text = right,
                FontSize = 11,
                Foreground = FlyoutPanelParts.TitleBrush,
            }
        );
        line.Margin = new Thickness(0);
        Child = line;
    }

    /// <summary>The aircraft and its runway: <c>N738SP · RWY 28R</c>, or the callsign alone with no runway.</summary>
    public string Right { get; }

    /// <summary>The line as one line, <c>Report when… · Right</c>.</summary>
    public override string ToString() => $"{ReportWhenFlyout.Title} · {Right}";
}

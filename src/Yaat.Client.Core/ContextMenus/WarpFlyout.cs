using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Warp flyout, Warp… and Warp here at a map point alike: a title line naming the aircraft and its present heading, altitude
/// and speed, then four box rows — Fix or FRD (a fix box with fix suggestions, led by the Warp glyph), Heading, Altitude and
/// Speed — each with the line under its box and its command column showing what it puts in the command, then the send line
/// showing the whole <c>WARP</c> command. Heading, altitude and speed are optional: a blank one before a filled one puts the
/// present value in its place, in amber, since the sim fills its slots left to right, and blanks at the end are left out
/// (<c>—</c>), the sim keeping the present values. Enter in any box, or a click on the send line, sends the whole command once
/// the fix reads and every field reads; a click on a box row only focuses its box. A box whose text does not read is refused
/// inline, and a blank altitude before a filled field is refused while the present altitude rounds below 100 ft, which no
/// altitude word reads back as.
/// </summary>
internal static partial class WarpFlyout
{
    /// <summary>The title line's title.</summary>
    public const string Title = "Warp";

    /// <summary>The reason the FRD box gives for text the navigation data does not know.</summary>
    public const string UnknownFix = "Not a known fix or FRD";

    /// <summary>The reason the Heading box gives for text that is not a heading from 001 to 360.</summary>
    public const string InvalidHeading = "Not a heading (001-360)";

    /// <summary>The FRD box's name.</summary>
    public const string FrdBoxName = "WarpFrdBox";

    /// <summary>The Heading box's name.</summary>
    public const string HeadingBoxName = "WarpHeadingBox";

    /// <summary>The Altitude box's name.</summary>
    public const string AltitudeBoxName = "WarpAltitudeBox";

    /// <summary>The Speed box's name.</summary>
    public const string SpeedBoxName = "WarpSpeedBox";

    /// <summary>
    /// The Warp flyout headed <paramref name="header"/> for <paramref name="aircraft"/>, its FRD box labelled and seeded by
    /// <paramref name="frd"/> and holding the focus as it opens.
    /// </summary>
    public static MenuItem Build(string header, IMenuAircraft aircraft, WarpFrdSeed frd, MenuContext context, IMenuHost host)
    {
        var rows = new InlineBoxRow?[4];
        string? WholeCommand() => Command(rows);
        Task Send(string command) => host.SendAsync(context.Callsign, command, context.Initials);
        Func<bool> FilledAfter(int index) => () => rows.Skip(index + 1).Any(row => (row is not null) && (row.Text.Trim().Length > 0));

        rows[0] = InlineBoxRow.CreateSendingAll(FrdSpec(frd, host), WholeCommand, Send);
        rows[1] = InlineBoxRow.CreateSendingAll(HeadingSpec(aircraft, FilledAfter(1)), WholeCommand, Send);
        rows[2] = InlineBoxRow.CreateSendingAll(AltitudeSpec(aircraft, FilledAfter(2)), WholeCommand, Send);
        rows[3] = InlineBoxRow.CreateSendingAll(SpeedSpec(aircraft, FilledAfter(3)), WholeCommand, Send);
        InlineBoxRow[] built = [.. rows.OfType<InlineBoxRow>()];
        var sendLine = new WarpSendLine(WholeCommand);
        MenuItem sendItem = sendLine.BuildItem(Send);

        void Refollow()
        {
            foreach (InlineBoxRow row in built)
            {
                row.RefollowSend();
            }

            sendLine.Show(sendItem);
        }

        var menu = new MenuItem { Header = header };
        menu.Items.Add(TitleItem(aircraft, context));
        foreach (InlineBoxRow row in built)
        {
            row.Changed += (_, _) => Refollow();
            menu.Items.Add(row.Item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(sendItem);
        Refollow();
        SharedMenuGroups.FocusTheBoxOnOpen(menu, built[0]);
        return menu;
    }

    /// <summary>
    /// The whole command the four rows make: <c>WARP {frd}</c>, then each field up to the last filled one, a blank one's kept
    /// present value in its place; null until every row is built, while the FRD box is blank or refused, or while a field is refused.
    /// </summary>
    private static string? Command(InlineBoxRow?[] rows)
    {
        if ((rows is not [{ } frd, { } heading, { } altitude, { } speed]) || (frd.Command is not { } fix))
        {
            return null;
        }

        InlineBoxRow[] fields = [heading, altitude, speed];
        return fields.Any(field => field.IsRefused)
            ? null
            : string.Join(' ', ["WARP", fix, .. fields.Select(field => field.Command).OfType<string>()]);
    }

    /// <summary>The disabled title line: <c>Warp</c>, then the aircraft with its present heading, altitude and speed.</summary>
    private static MenuItem TitleItem(IMenuAircraft aircraft, MenuContext context)
    {
        string now = $"{context.Callsign} · now {PresentHeading(aircraft)}° · {Feet(aircraft.AltitudeFeet)} ft · {PresentSpeed(aircraft)} kt";
        var title = new WarpTitleLine(now);
        var item = new MenuItem { Header = title, IsEnabled = false };
        AutomationProperties.SetName(item, title.ToString());
        return item;
    }

    private static InlineBoxSpec FrdSpec(WarpFrdSeed frd, IMenuHost host) =>
        new()
        {
            Label = frd.Label,
            Glyph = QuickCommandGlyphs.ById[MenuIds.SimControlWarp],
            BlankGlyphSlot = false,
            Verb = "",
            Placeholder = "e.g. SUNOL or SFO090015",
            BoxName = FrdBoxName,
            BoxWidth = 220,
            Seed = frd.Text,
            FixSuggestions = host.SuggestFixes,
            Read = text => ReadFrd(text, host),
        };

    private static InlineBoxSpec HeadingSpec(IMenuAircraft aircraft, Func<bool> filledAfter)
    {
        string present = PresentHeading(aircraft);
        var blank = new BlankField(filledAfter, Kept(present), $"left out: the sim keeps {present}");
        return FieldSpec("Heading (magnetic)", $"keep {present}", HeadingBoxName, text => ReadField(text, blank, ReadHeading));
    }

    private static InlineBoxSpec AltitudeSpec(IMenuAircraft aircraft, Func<bool> filledAfter)
    {
        string feet = Feet(aircraft.AltitudeFeet);
        InlineBoxReading beforeFilled =
            (PresentAltitudeHundreds(aircraft) is { } hundreds)
                ? Kept(hundreds)
                : InlineBoxReading.Refuses(
                    $"Blank before a filled field, and the present {feet} ft has no word the sim reads as it: type an altitude"
                );
        var blank = new BlankField(filledAfter, beforeFilled, $"left out: the sim keeps {feet} ft");
        string placeholder = $"keep {AltitudeFlyout.PresentText(aircraft.AltitudeFeet)}";
        return FieldSpec("Altitude (hundreds, feet or FL)", placeholder, AltitudeBoxName, text => ReadField(text, blank, ReadAltitude));
    }

    private static InlineBoxSpec SpeedSpec(IMenuAircraft aircraft, Func<bool> filledAfter)
    {
        string present = PresentSpeed(aircraft);
        var blank = new BlankField(filledAfter, Kept(present), $"left out: the sim keeps {present} kt");
        return FieldSpec("Speed (knots IAS)", $"keep {present}", SpeedBoxName, text => ReadField(text, blank, ReadSpeed));
    }

    private static InlineBoxSpec FieldSpec(string label, string placeholder, string boxName, Func<string, InlineBoxReading> read) =>
        new()
        {
            Label = label,
            Glyph = null,
            BlankGlyphSlot = true,
            Verb = "",
            Placeholder = placeholder,
            BoxName = boxName,
            BoxWidth = 120,
            Seed = "",
            FixSuggestions = null,
            Read = read,
        };

    /// <summary>
    /// The FRD box's reading: the fix or FRD the navigation data knows (<see cref="FixList.TypedFixOrFrd"/>), an FRD read back
    /// under the box (<see cref="FrdReading"/>). Blank waits, and so does a partial name without digits that the fix
    /// suggestions still offer names for (<see cref="IMenuHost.SuggestFixes"/>); anything else is refused.
    /// </summary>
    private static InlineBoxReading ReadFrd(string text, IMenuHost host)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return InlineBoxReading.Blank;
        }

        if (FixList.TypedFixOrFrd(trimmed) is { } frd)
        {
            return (FrdReading(frd) is { } reading) ? InlineBoxReading.Reads(frd, reading) : InlineBoxReading.Reads(frd, $"{frd} · fix");
        }

        bool suggesting = !trimmed.Any(char.IsAsciiDigit) && (host.SuggestFixes(trimmed).Count > 0);
        return suggesting ? InlineBoxReading.Blank : InlineBoxReading.Refuses(UnknownFix);
    }

    /// <summary>
    /// A known fix-radial-distance read back: <c>10 NM on the 090° radial of OAK (magnetic)</c>; null for a plain fix, whose name
    /// the menu has no lookup for.
    /// </summary>
    private static string? FrdReading(string frd)
    {
        Match parts = FrdParts().Match(frd);
        if ((!parts.Success) || (!FixList.IsFrd(frd)))
        {
            return null;
        }

        int distance = int.Parse(parts.Groups[3].Value, CultureInfo.InvariantCulture);
        return $"{distance} NM on the {parts.Groups[2].Value}° radial of {parts.Groups[1].Value} (magnetic)";
    }

    [GeneratedRegex("^([A-Z]{2,5})([0-9]{3})([0-9]{3})$")]
    private static partial Regex FrdParts();

    /// <summary>
    /// A field's reading: typed text as <paramref name="readTyped"/> reads it; a blank one before a filled field as
    /// <paramref name="blank"/>'s reading before a filled field, else left out.
    /// </summary>
    private static InlineBoxReading ReadField(string text, BlankField blank, Func<string, InlineBoxReading> readTyped)
    {
        if (text.Trim().Length > 0)
        {
            return readTyped(text);
        }

        return blank.FilledAfter() ? blank.BeforeFilled : InlineBoxReading.LeavesOut(blank.LeftOut);
    }

    /// <summary>A blank field's present value put in the command, with the line saying why.</summary>
    private static InlineBoxReading Kept(string word) =>
        InlineBoxReading.Keeps(word, $"kept: {word} sent in its place, since a gap would shift the slots");

    /// <summary>A typed heading (<see cref="FixValueFlyout.ParseHeading"/>) as three digits.</summary>
    private static InlineBoxReading ReadHeading(string text)
    {
        if (FixValueFlyout.ParseHeading(text) is not { } heading)
        {
            return InlineBoxReading.Refuses(InvalidHeading);
        }

        string word = heading.ToString("D3", CultureInfo.InvariantCulture);
        return InlineBoxReading.Reads(word, $"heading {word} magnetic");
    }

    /// <summary>
    /// A typed altitude (<see cref="MenuCatalog.ParseAltitudeFeet"/>), 100 ft or more, in the hundreds the sim reads back as the
    /// same feet (<c>8</c> for 800 ft), read back as the feet it sends.
    /// </summary>
    private static InlineBoxReading ReadAltitude(string text)
    {
        if ((MenuCatalog.ParseAltitudeFeet(text) is not { } feet) || (feet < 100))
        {
            return InlineBoxReading.Refuses(AltitudeFlyout.InvalidAltitude);
        }

        int hundreds = feet / 100;
        return InlineBoxReading.Reads(hundreds.ToString(CultureInfo.InvariantCulture), $"→ {Feet(hundreds * 100)} ft");
    }

    /// <summary>A typed speed: a positive whole number of knots.</summary>
    private static InlineBoxReading ReadSpeed(string text)
    {
        string trimmed = text.Trim();
        if (
            (!trimmed.All(char.IsAsciiDigit))
            || (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int knots))
            || (knots <= 0)
        )
        {
            return InlineBoxReading.Refuses(SpeedFlyout.InvalidSpeed);
        }

        string word = knots.ToString(CultureInfo.InvariantCulture);
        return InlineBoxReading.Reads(word, $"{word} kt indicated");
    }

    /// <summary>The aircraft's magnetic heading, rounded, as three digits; a zero or negative one is 360.</summary>
    private static string PresentHeading(IMenuAircraft aircraft)
    {
        int heading = Rounded(aircraft.MagneticHeadingDegrees);
        return ((heading <= 0) ? 360 : heading).ToString("D3", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The aircraft's altitude rounded to the nearest 100 ft, in hundreds; null below 50 ft, which rounds to no hundreds the sim
    /// reads as an altitude.
    /// </summary>
    private static string? PresentAltitudeHundreds(IMenuAircraft aircraft)
    {
        int hundreds = Rounded(aircraft.AltitudeFeet / 100);
        return (hundreds >= 1) ? hundreds.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The aircraft's indicated airspeed, rounded to whole knots.</summary>
    private static string PresentSpeed(IMenuAircraft aircraft) => Rounded(aircraft.IndicatedAirspeedKnots).ToString(CultureInfo.InvariantCulture);

    /// <summary>Feet rounded to whole feet with thousands grouped: <c>11,000</c>.</summary>
    private static string Feet(double feet) => Rounded(feet).ToString("N0", CultureInfo.InvariantCulture);

    private static int Rounded(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How a field reads blank: <see cref="BeforeFilled"/> while <see cref="FilledAfter"/> holds (a later field is filled),
    /// else left out, <see cref="LeftOut"/> naming what the sim keeps.
    /// </summary>
    private sealed record BlankField(Func<bool> FilledAfter, InlineBoxReading BeforeFilled, string LeftOut);
}

/// <summary>What the Warp flyout's FRD box opens with: its label and its text.</summary>
/// <param name="Label">The box's label.</param>
/// <param name="Text">The text the box opens holding, selected; empty for none.</param>
internal readonly record struct WarpFrdSeed(string Label, string Text)
{
    /// <summary>Warp…'s FRD box: empty.</summary>
    public static WarpFrdSeed Empty { get; } = new("Fix or FRD", "");

    /// <summary>Warp here's FRD box at a map point: holding the clicked point's fix-radial-distance, <paramref name="frd"/>.</summary>
    public static WarpFrdSeed ClickedPoint(string frd) => new("Fix or FRD · the clicked point", frd);
}

/// <summary>The Warp flyout's title line: <c>Warp</c>, then <see cref="Now"/> right, in the monospace font.</summary>
internal sealed class WarpTitleLine : Decorator
{
    /// <summary>A title line reading <paramref name="now"/> after the title.</summary>
    public WarpTitleLine(string now)
    {
        Now = now;
        Grid line = FlyoutPanelParts.TitleLine(
            new TextBlock
            {
                Text = WarpFlyout.Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            },
            new TextBlock
            {
                Text = now,
                FontSize = 11,
                Foreground = FlyoutPanelParts.TitleBrush,
            }
        );
        line.Margin = new Thickness(0);
        Child = line;
    }

    /// <summary>The aircraft and its present heading, altitude and speed: <c>N123AB · now 130° · 800 ft · 85 kt</c>.</summary>
    public string Now { get; }

    /// <summary>The line as one line, <c>Warp · Now</c>.</summary>
    public override string ToString() => $"{WarpFlyout.Title} · {Now}";
}

/// <summary>
/// The Warp flyout's last row: "Enter in any box sends", then the whole command in the command column at full strength, or
/// <c>can't send</c> in red while there is none (the FRD box blank or refused, or a field refused).
/// </summary>
internal sealed class WarpSendLine : Grid
{
    /// <summary>The send line's label.</summary>
    public const string Label = "Enter in any box sends";

    private readonly Func<string?> _command;

    /// <summary>A send line showing <paramref name="command"/>, or <c>can't send</c> while it is null.</summary>
    public WarpSendLine(Func<string?> command)
    {
        _command = command;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Children.Add(new TextBlock { Text = Label, VerticalAlignment = VerticalAlignment.Center });
        CommandColumn = FlyoutPanelParts.CommandText("");
        SetColumn(CommandColumn, 1);
        Children.Add(CommandColumn);
    }

    /// <summary>The command column.</summary>
    public TextBlock CommandColumn { get; }

    /// <summary>
    /// The send line's menu item: a click hands the whole command to <paramref name="send"/>; while there is none it stays
    /// open and sends nothing.
    /// </summary>
    public MenuItem BuildItem(Func<string, Task> send)
    {
        var item = new MenuItem { Header = this };
        item.Click += async (_, e) =>
        {
            if (_command() is not { } command)
            {
                e.Handled = true;
                return;
            }

            await send(command);
        };
        return item;
    }

    /// <summary>Shows the whole command now on the line and on <paramref name="item"/>.</summary>
    public void Show(MenuItem item)
    {
        string? command = _command();
        CommandColumn.Opacity = 1;
        if (command is not null)
        {
            CommandColumn.Text = command;
            CommandColumn.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            CommandColumn.Text = InlineBoxRow.CantSend;
            CommandColumn.Foreground = FlyoutPanelParts.RefusalBrush;
        }

        MenuCommandText.SetCommand(item, command);
        AutomationProperties.SetName(item, ToString());
        item.StaysOpenOnClick = command is null;
    }

    /// <summary>The line as one line, <c>Label — Command</c>.</summary>
    public override string ToString() => $"{Label} — {CommandColumn.Text}";
}

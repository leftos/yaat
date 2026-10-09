using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// An approach picker entry's label, the command it sends, and the label of its default-approach leaf for an approach's short name.
/// </summary>
internal sealed record ApproachPickerSpec(string Label, string Command, Func<string, string> LeafLabel);

/// <summary>
/// Builds the approach pickers (<see cref="MenuCatalog.ApproachPickerIds"/>). With a default approach
/// (<see cref="DefaultApproach"/>) an entry is a one-click leaf naming it, and its "(other)" companion
/// (<see cref="BuildOther"/>) is the grouped picker; without one the entry is the grouped picker itself; with no
/// destination approaches it is free text. The grouped picker lists the default runway's approaches first, by kind, then
/// one submenu per other runway, circling approaches last.
/// </summary>
internal static class ApproachPickerBuilder
{
    /// <summary>The badge on the expected approach's row in a grouped picker.</summary>
    private const string ExpectedBadge = "expected";

    /// <summary>The label of the submenu holding the approaches that name no runway.</summary>
    private const string CirclingLabel = "Circling";

    private static readonly IImmutableSolidColorBrush BadgeBrush = new ImmutableSolidColorBrush(Color.Parse("#E0A84A"));

    /// <summary>
    /// The entry's item, by the data present when the menu is built: the default approach's leaf, else the grouped picker
    /// under the entry's label, else free text under the label with an ellipsis when the destination has no approaches.
    /// </summary>
    public static MenuItem Build(ApproachPickerSpec spec, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<ApproachChoice> choices = Choices(aircraft);
        if (choices.Count == 0)
        {
            return MenuCatalog.BuildInput(
                $"{spec.Label}{MenuCatalog.Ellipsis}",
                "Approach ID",
                BlankInput.Closes,
                text => $"{spec.Command} {text}",
                context,
                host
            );
        }

        if (DefaultApproach(aircraft, choices) is { } chosen)
        {
            return MenuCatalog.BuildSend(spec.LeafLabel(chosen.ShortName), $"{spec.Command} {chosen.Id}", context, host);
        }

        return BuildGrouped(spec.Label, aircraft, choices, new ApproachRowSender(spec.Command, null, context, host));
    }

    /// <summary>
    /// The entry's companion, which shares its id: the grouped picker labelled "(other)", offered beside a default
    /// approach; null without a default.
    /// </summary>
    public static MenuItem? BuildOther(ApproachPickerSpec spec, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<ApproachChoice> choices = Choices(aircraft);
        if (DefaultApproach(aircraft, choices) is null)
        {
            return null;
        }

        string? expectedId = Expected(aircraft, choices)?.Id;
        return BuildGrouped(
            $"{spec.Label} (other){MenuCatalog.Ellipsis}",
            aircraft,
            choices,
            new ApproachRowSender(spec.Command, expectedId, context, host)
        );
    }

    /// <summary>
    /// The approach a picker defaults to: the expected approach when it is one of the destination's, else the assigned
    /// runway's first approach in picker order (its ILS when it has one); null with neither, or when the assigned runway
    /// has no approach.
    /// </summary>
    private static ApproachChoice? DefaultApproach(IMenuAircraft? aircraft, List<ApproachChoice> choices)
    {
        if (Expected(aircraft, choices) is { } expected)
        {
            return expected;
        }

        string? assigned = aircraft?.AssignedRunway;
        return string.IsNullOrEmpty(assigned) ? null : choices.FirstOrDefault(choice => SameRunway(choice.Runway, assigned));
    }

    /// <summary>
    /// The expected approach among <paramref name="choices"/>, its id resolved from the controller's shorthand
    /// (<c>R28L</c> for <c>R28LY</c>) as the sim resolves it; null when none is expected or it is not the destination's.
    /// </summary>
    private static ApproachChoice? Expected(IMenuAircraft? aircraft, List<ApproachChoice> choices)
    {
        if (aircraft is not { Destination.Length: > 0, ExpectedApproach: { Length: > 0 } expected })
        {
            return null;
        }

        string id = NavigationDatabase.Instance.ResolveApproachId(aircraft.Destination, expected) ?? expected;
        return choices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a normalized approach runway is the runway <paramref name="designator"/> names; never for a circling approach.</summary>
    private static bool SameRunway(string runway, string designator) =>
        (runway.Length > 0) && string.Equals(runway, RunwayIdentifier.NormalizeDesignator(designator), StringComparison.OrdinalIgnoreCase);

    /// <summary>The destination's approaches in picker order: by kind (<see cref="ApproachChoice.KindRank"/>), then by id.</summary>
    private static List<ApproachChoice> Choices(IMenuAircraft? aircraft)
    {
        IReadOnlyList<CifpApproachProcedure> approaches = MenuCatalog.DestinationApproaches(aircraft);
        return
        [
            .. approaches
                .Select(ToChoice)
                .OrderBy(choice => choice.KindRank)
                .ThenBy(choice => choice.Kind, StringComparer.Ordinal)
                .ThenBy(choice => choice.Id, StringComparer.Ordinal),
        ];
    }

    private static ApproachChoice ToChoice(CifpApproachProcedure procedure) =>
        string.IsNullOrEmpty(procedure.Runway) ? ToCirclingChoice(procedure.ApproachId) : ToRunwayChoice(procedure);

    /// <summary>A runway approach: its kind from the CIFP type code, its variant after the runway or the dash (<c>R28LY</c>, <c>R30-Y</c>).</summary>
    private static ApproachChoice ToRunwayChoice(CifpApproachProcedure procedure)
    {
        string id = procedure.ApproachId;
        string runway = procedure.Runway!;
        (string Kind, string FullKind) kind = RunwayApproachKind(procedure.TypeCode);
        int dash = id.LastIndexOf('-');
        int at = id.IndexOf(runway, 1, StringComparison.Ordinal);
        string variant =
            (dash >= 0) ? id[(dash + 1)..]
            : (at < 0) ? ""
            : id[(at + runway.Length)..];
        return new(id, kind.Kind, kind.FullKind, variant, RunwayIdentifier.NormalizeDesignator(runway));
    }

    /// <summary>An approach that names no runway, as the pickers show it (its runway is empty).</summary>
    private static ApproachChoice ToCirclingChoice(string id)
    {
        (string kind, string fullKind, string variant) = DescribeCirclingApproach(id);
        return new(id, kind, fullKind, variant, "");
    }

    /// <summary>
    /// A circling approach id's kind and variant. CIFP's type code is the id's first letter, which for these ids is not
    /// the ARINC type, so the kind is read from the id's prefix: the letters before the dash (<c>VDM-A</c>), else all but
    /// the last letter (<c>RNVA</c>); the variant is the letter after it.
    /// </summary>
    internal static (string Kind, string FullKind, string Variant) DescribeCirclingApproach(string id)
    {
        int dash = id.IndexOf('-');
        (string Prefix, string Variant) split =
            (dash > 0) ? (id[..dash], id[(dash + 1)..])
            : (id.Length > 1) ? (id[..^1], id[^1..])
            : (id, "");
        (string Kind, string FullKind) kind = CirclingApproachKind(split.Prefix);
        return (kind.Kind, kind.FullKind, split.Variant);
    }

    /// <summary>
    /// A runway approach's kind by its ARINC 424 type code: the short kind the pickers group and name it by, and the kind
    /// as the published procedure name spells it (<c>RNAV (GPS)</c>, <c>RNAV (RNP)</c>).
    /// </summary>
    private static (string Kind, string FullKind) RunwayApproachKind(char typeCode) =>
        typeCode switch
        {
            'I' => ("ILS", "ILS"),
            'L' => ("LOC", "LOC"),
            'R' => ("RNAV", "RNAV (GPS)"),
            'H' => ("RNP", "RNAV (RNP)"),
            'B' => ("LOC BC", "LOC BC"),
            'D' => ("VOR/DME", "VOR/DME"),
            'S' or 'V' => ("VOR", "VOR"),
            'N' => ("NDB", "NDB"),
            'Q' => ("NDB/DME", "NDB/DME"),
            'P' => ("GPS", "GPS"),
            'J' => ("GLS", "GLS"),
            'X' => ("LDA", "LDA"),
            'U' => ("SDF", "SDF"),
            'T' => ("TACAN", "TACAN"),
            'G' => ("IGS", "IGS"),
            'F' => ("FMS", "FMS"),
            'M' or 'W' or 'Y' => ("MLS", "MLS"),
            _ => (typeCode.ToString(), typeCode.ToString()),
        };

    /// <summary>A circling approach's kind by its id prefix (<c>VDM</c>, <c>RNV</c>, <c>LBC</c>); an unknown prefix names itself.</summary>
    private static (string Kind, string FullKind) CirclingApproachKind(string prefix) =>
        prefix switch
        {
            "VDM" => ("VOR/DME", "VOR/DME"),
            "VOR" => ("VOR", "VOR"),
            "RNV" => ("RNAV", "RNAV (GPS)"),
            "GPS" => ("GPS", "GPS"),
            "LBC" => ("LOC BC", "LOC BC"),
            "LDA" => ("LDA", "LDA"),
            "LOC" => ("LOC", "LOC"),
            "NDB" => ("NDB", "NDB"),
            "NDM" => ("NDB/DME", "NDB/DME"),
            _ => (prefix, prefix),
        };

    /// <summary>
    /// The grouped picker under <paramref name="header"/>: the default runway's approaches first
    /// (<see cref="MenuCatalog.SmartVisualRunway"/>, headed <c>Runway 30 · assigned</c> when it is the assigned runway),
    /// grouped by kind, then <c>Other runways</c> with one submenu per other runway (<c>28R · ILS, LOC, RNAV Y, RNP Z</c>)
    /// in runway order, circling approaches last. With no default runway, or one without approaches, every runway is a
    /// submenu at the top level. Its descriptor names each group on one line for a menu walker.
    /// </summary>
    private static MenuItem BuildGrouped(string header, IMenuAircraft? aircraft, List<ApproachChoice> choices, ApproachRowSender sender)
    {
        var picker = new MenuItem { Header = header };
        List<IGrouping<string, ApproachChoice>> runways =
        [
            .. choices
                .GroupBy(choice => choice.Runway, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key.Length == 0)
                .ThenBy(group => group.Key, RunwayDesignatorComparer.Instance),
        ];
        List<string> summary = [];
        string? defaultRunway = MenuCatalog.SmartVisualRunway(aircraft);
        IGrouping<string, ApproachChoice>? first =
            (defaultRunway is null) ? null : runways.FirstOrDefault(group => SameRunway(group.Key, defaultRunway));
        if (first is not null)
        {
            string runway = RunwayIdentifier.ToDisplayDesignator(first.Key);
            string section = string.IsNullOrEmpty(aircraft?.AssignedRunway) ? $"Runway {runway}" : $"Runway {runway} · assigned";
            picker.Items.Add(HeaderItem(section, isSection: true));
            AddRows(picker.Items, first, sender);
            summary.Add($"{section}: {string.Join(", ", first.Select(choice => choice.Id))}");
            runways.Remove(first);
            if (runways.Count > 0)
            {
                picker.Items.Add(new Separator());
                picker.Items.Add(HeaderItem("Other runways", isSection: true));
                summary.Add($"Other runways: {string.Join(", ", runways.Select(RunwayName))}");
            }
        }
        else
        {
            summary.Add(string.Join(", ", runways.Select(RunwayName)));
        }

        foreach (IGrouping<string, ApproachChoice> group in runways)
        {
            var submenu = new MenuItem { Header = $"{RunwayName(group)} · {string.Join(", ", group.Select(choice => choice.ShortKind))}" };
            AddRows(submenu.Items, group, sender);
            picker.Items.Add(submenu);
        }

        picker.Tag = new MenuPickerDescriptor(MenuPickerDescriptor.Grouped, summary);
        return picker;
    }

    /// <summary>A runway group's name: the runway as displayed, or <c>Circling</c> for the approaches that name none.</summary>
    private static string RunwayName(IGrouping<string, ApproachChoice> group) =>
        (group.Key.Length > 0) ? RunwayIdentifier.ToDisplayDesignator(group.Key) : CirclingLabel;

    /// <summary>Adds <paramref name="approaches"/>' rows, each run of one kind under a header naming it.</summary>
    private static void AddRows(ItemCollection items, IEnumerable<ApproachChoice> approaches, ApproachRowSender sender)
    {
        string? kind = null;
        foreach (ApproachChoice approach in approaches)
        {
            if (!string.Equals(approach.Kind, kind, StringComparison.Ordinal))
            {
                kind = approach.Kind;
                items.Add(HeaderItem(kind, isSection: false));
            }

            items.Add(sender.Row(approach));
        }
    }

    /// <summary>
    /// A disabled header item: a section (<c>Runway 30 · assigned</c>, <c>Other runways</c>) in semibold, or a kind
    /// (<c>ILS</c>) dimmed.
    /// </summary>
    private static MenuItem HeaderItem(string text, bool isSection) =>
        new()
        {
            Header = text,
            IsEnabled = false,
            FontSize = 11,
            FontWeight = isSection ? FontWeight.SemiBold : FontWeight.Normal,
            Opacity = isSection ? 1.0 : 0.8,
        };

    /// <summary>A row's view: the name, the badge outlined in amber, and the command right-aligned in the dimmed monospace font.</summary>
    private static Grid RowView(ApproachRowHeader row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center });
        if (row.Badge is { } badge)
        {
            var badgeView = new Border
            {
                BorderBrush = BadgeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badge,
                    FontSize = 11,
                    Foreground = BadgeBrush,
                },
            };
            Grid.SetColumn(badgeView, 1);
            grid.Children.Add(badgeView);
        }

        var command = new TextBlock
        {
            Text = row.Command,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        return grid;
    }

    /// <summary>A destination approach as the pickers show it, its runway normalized (empty for a circling approach).</summary>
    private sealed record ApproachChoice(string Id, string Kind, string FullKind, string Variant, string Runway)
    {
        /// <summary>The kind's place in a picker: ILS, LOC, RNAV, RNP, then every other kind alphabetically.</summary>
        public int KindRank =>
            Kind switch
            {
                "ILS" => 0,
                "LOC" => 1,
                "RNAV" => 2,
                "RNP" => 3,
                _ => 4,
            };

        /// <summary>The kind and variant, as a runway submenu lists them: <c>ILS</c>, <c>RNP Z</c>, a circling <c>VOR/DME-A</c>.</summary>
        public string ShortKind =>
            (Variant.Length == 0) ? Kind
            : (Runway.Length > 0) ? $"{Kind} {Variant}"
            : $"{Kind}-{Variant}";

        /// <summary>
        /// The short name a default leaf shows: <c>ILS 30</c>, <c>RNAV Y 28L</c>. An RNAV (RNP) approach is named RNAV, as
        /// a clearance names it (7110.65 4-8-1); circling approaches go by <see cref="ShortKind"/>.
        /// </summary>
        public string ShortName
        {
            get
            {
                if (Runway.Length == 0)
                {
                    return ShortKind;
                }

                string kind = (Kind == "RNP") ? "RNAV" : Kind;
                string runway = RunwayIdentifier.ToDisplayDesignator(Runway);
                return (Variant.Length > 0) ? $"{kind} {Variant} {runway}" : $"{kind} {runway}";
            }
        }

        /// <summary>The procedure's name as published: <c>ILS RWY 30</c>, <c>RNAV (GPS) Y RWY 30</c>, a circling <c>VOR/DME-A</c>.</summary>
        public string FullName
        {
            get
            {
                if (Runway.Length == 0)
                {
                    return (Variant.Length > 0) ? $"{FullKind}-{Variant}" : FullKind;
                }

                string kind = (Variant.Length > 0) ? $"{FullKind} {Variant}" : FullKind;
                return $"{kind} RWY {RunwayIdentifier.ToDisplayDesignator(Runway)}";
            }
        }
    }

    /// <summary>What a grouped picker's row shows: the procedure's name, its badge (or none) and the command it sends.</summary>
    private sealed record ApproachRowHeader(string Name, string? Badge, string Command)
    {
        /// <summary>The row as one line of text: <c>ILS RWY 30 · expected — CAPP I30</c>.</summary>
        public override string ToString() => (Badge is null) ? $"{Name} — {Command}" : $"{Name} · {Badge} — {Command}";
    }

    /// <summary>Builds a grouped picker's rows: each sends <see cref="Command"/> with its approach, the expected approach's row badged.</summary>
    private sealed record ApproachRowSender(string Command, string? ExpectedId, MenuContext Context, IMenuHost Host)
    {
        public MenuItem Row(ApproachChoice approach)
        {
            string command = $"{Command} {approach.Id}";
            var header = new ApproachRowHeader(approach.FullName, (approach.Id == ExpectedId) ? ExpectedBadge : null, command);
            return MenuCatalog.BuildTemplatedSend(header, new FuncDataTemplate<ApproachRowHeader>((row, _) => RowView(row)), command, Context, Host);
        }
    }
}

using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;
using ApproachChoice = Yaat.Client.ContextMenus.ApproachChoices.ApproachChoice;

namespace Yaat.Client.ContextMenus;

/// <summary>Which visual rows an approach flyout offers.</summary>
public enum ApproachVisualRows
{
    /// <summary>None: the verb has no visual form (a visual approach has no fixes to join or course to intercept).</summary>
    None,

    /// <summary>
    /// Visual clearances (<c>CVA {rwy}</c>, the untowered airport row <c>CVA {airport}</c>) to an IFR aircraft only, each greyed
    /// until the field is reported in sight.
    /// </summary>
    Clearance,

    /// <summary>Visual expectations (<c>EAPP VIS{rwy}</c>) to any aircraft, never greyed.</summary>
    Expectation,
}

/// <summary>
/// An approach flyout's verb, fixed by the All Commands entry that opens it (<see cref="MenuCatalog.ApproachPickerIds"/>):
/// its label, the command its rows send, whether they are forced, and what else the flyout offers.
/// </summary>
public sealed record ApproachVerbSpec
{
    /// <summary>The entry's label and the flyout's title: <c>Cleared approach</c>, <c>Join approach (force)</c>.</summary>
    public required string Label { get; init; }

    /// <summary>The bare verb an instrument row and the Approach box send with an approach id: <c>CAPP</c>, <c>JAPPF</c>.</summary>
    public required string Verb { get; init; }

    /// <summary>Whether every row sends a forced form and carries the force mark, and the title line too (the force pair).</summary>
    public required bool IsForced { get; init; }

    /// <summary>The visual rows the flyout offers.</summary>
    public required ApproachVisualRows Visuals { get; init; }

    /// <summary>The forced verb the Force approach toggle switches the rows to (<c>CAPPF</c>), or null when the flyout has no toggle.</summary>
    public required string? ForceToggleVerb { get; init; }

    /// <summary>Whether the flyout offers Cancel IFR (to an IFR aircraft airborne below FL180).</summary>
    public required bool OffersCancelIfr { get; init; }

    /// <summary>The catalog id whose glyph (<see cref="QuickCommandGlyphs.For"/>) the flyout's instrument rows and box carry.</summary>
    public required string GlyphId { get; init; }

    /// <summary>The default row's label for an approach's short name (<c>Join ILS 30</c>) or <c>visual {runway}</c>.</summary>
    public required Func<string, string> DefaultLabel { get; init; }
}

/// <summary>What an approach flyout row stands for.</summary>
public enum ApproachRowKind
{
    /// <summary>A published instrument approach (<c>ILS RWY 30</c>).</summary>
    Instrument,

    /// <summary>A runway's visual approach (<c>Visual RWY 30</c>).</summary>
    Visual,

    /// <summary>The airport-level visual approach at an untowered field, the pilot picking the runway (<c>CVA KHES</c>).</summary>
    Airport,
}

/// <summary>The report a visual row waits for before it goes live without Force approach.</summary>
public enum ApproachRowSight
{
    /// <summary>No report: an instrument row, or any Expect approach row.</summary>
    None,

    /// <summary>The field in sight (<c>RFIS</c>): a visual row without FOLLOW.</summary>
    Field,

    /// <summary>The traffic in sight (<c>RTIS</c>): a visual row with FOLLOW.</summary>
    Traffic,
}

/// <summary>Where the flyout places its Cancel IFR row.</summary>
public enum CancelIfrPlacement
{
    /// <summary>Pinned above the tabs: the destination has no instrument approach.</summary>
    Top,

    /// <summary>The last row, after the Approach box, behind a separator.</summary>
    Bottom,
}

/// <summary>The flyout's Cancel IFR row: where it sits and the question its confirmation strip asks.</summary>
/// <param name="Placement">Where the row sits.</param>
/// <param name="Question">The confirmation's wording.</param>
public sealed record ApproachCancelIfr(CancelIfrPlacement Placement, string Question)
{
    /// <summary>The row's label.</summary>
    public const string Label = "Cancel IFR…";

    /// <summary>The command Confirm sends.</summary>
    public const string Command = "CIFR";
}

/// <summary>One runway's tab, or the Circling tab: its label with its row count (<c>30 5</c>, <c>Circling 1</c>).</summary>
/// <param name="Label">The tab's text.</param>
/// <param name="Runway">The runway in display form, or <see cref="ApproachFlyoutModel.CirclingTab"/>.</param>
public sealed record ApproachFlyoutTab(string Label, string Runway);

/// <summary>
/// One approach flyout row as it shows with Force approach on or off: the name, the badge (<c>default</c>, <c>expected</c>),
/// the force mark, the notes, the kind and the command; disabled while it waits for a report Force approach did not override.
/// </summary>
public sealed record ApproachRow
{
    /// <summary>What the row stands for.</summary>
    public required ApproachRowKind Kind { get; init; }

    /// <summary>The row's name: <c>ILS RWY 30</c>, <c>Visual RWY 30</c>, <c>Cleared ILS 30</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The id the Approach box matches typing against: <c>I30</c>, <c>VIS30</c>, the airport.</summary>
    public required string MatchId { get; init; }

    /// <summary>The kind column: <c>ILS</c>, <c>VIS</c>.</summary>
    public required string KindText { get; init; }

    /// <summary>What the row sends.</summary>
    public required string Command { get; init; }

    /// <summary>The outlined badge after the name (<see cref="ApproachFlyoutModel.DefaultBadge"/>, <c>expected</c>), or null.</summary>
    public required string? Badge { get; init; }

    /// <summary>Whether the row sends the forced form (<c>CVAF</c>, <c>CAPPF</c>), drawn with the force mark.</summary>
    public required bool IsForced { get; init; }

    /// <summary>
    /// Whether the runway is too short for the type (<see cref="Yaat.Sim.Phases.RunwayLandability.IsLandable"/>), dimmed but
    /// clickable.
    /// </summary>
    public required bool IsShort { get; init; }

    /// <summary>A fixed note after the name (<c>pilot picks the runway</c>), or null.</summary>
    public required string? Note { get; init; }

    /// <summary>The greyed row's note (<c>field not in sight</c>) while it waits for a report; null when live.</summary>
    public required string? NotInSight { get; init; }

    /// <summary>The sim's refusal wording the greyed row's tooltip shows; null when live.</summary>
    public required string? Refusal { get; init; }

    /// <summary>The force mark's text.</summary>
    public const string ForceMark = "force";

    /// <summary>Whether the row can be clicked and focused: false while it waits for a report.</summary>
    public bool IsEnabled => NotInSight is null;

    /// <summary>Whether the row is drawn at the dimmed opacity: short, or waiting for a report.</summary>
    public bool IsDimmed => IsShort || !IsEnabled;

    /// <summary>The row's tooltip: the sim's refusal while greyed, else the row's whole text.</summary>
    public string Tooltip => Refusal ?? ToString();

    /// <summary>The notes after the name, in order: the fixed note, <c>short</c>, then the greyed note.</summary>
    public IReadOnlyList<string> Notes => [.. new[] { Note, IsShort ? "short" : null, NotInSight }.OfType<string>()];

    /// <summary>Whether the Approach box's <paramref name="typed"/> text is a prefix of the row's id, ignoring case.</summary>
    public bool Matches(string typed) => MatchId.StartsWith(typed.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The row as one line: <c>ILS RWY 30 · expected · force · short — CAPPF I30</c>.</summary>
    public override string ToString()
    {
        string?[] marks = [Badge, IsForced ? ForceMark : null];
        IEnumerable<string> parts = new[] { Name }.Concat(marks.OfType<string>()).Concat(Notes);
        return $"{string.Join(" · ", parts)} — {Command}";
    }
}

/// <summary>
/// What an approach flyout (one per <see cref="ApproachVerbSpec"/>) shows for an aircraft, built once when the flyout opens. The tabs are
/// the destination's runway ends: the room's arrival ends first in the room's order (<see cref="IMenuHost.GetActiveArrivalEnds"/>),
/// then the rest in the pattern-entry order (<see cref="IMenuHost.GetPatternRunwayChoices"/>, landable before short), then any
/// runway with an approach the list does not name, then a
/// last <c>Circling</c> tab for the approaches that name no runway. A runway tab holds its visual row first, then its
/// instrument approaches in flyout order (<see cref="ApproachChoices.Choices"/>); a row on a runway too short for the
/// type is dimmed "short". The default approach (the expected approach, else an expected visual, else the assigned runway's
/// first approach) is pinned above the tabs and its tab selected; without one the default runway's tab
/// (<see cref="MenuCatalog.SmartVisualRunway"/>) is, else the first. Only Cleared approach and Expect approach have visual
/// rows; Cleared approach offers them (and the airport row) to an IFR aircraft only, as the sim clears no other for a visual
/// approach; a runway left with no row gets no tab. In Cleared approach a visual row waits, greyed, for the
/// field in sight unless Force approach (offered outside solo training) is on, which turns every row forced; the force pair's
/// rows are always forced. At an untowered field Cleared approach pins the airport-level visual row; with no instrument
/// approach there it is the only row. Cleared approach and Expect approach offer Cancel IFR to an IFR aircraft airborne below
/// FL180.
/// </summary>
public sealed class ApproachFlyoutModel
{
    /// <summary>The <see cref="ApproachFlyoutTab.Runway"/> and label of the tab holding the approaches that name no runway.</summary>
    public const string CirclingTab = "Circling";

    /// <summary>The sim's refusal of a visual approach without the field in sight (<c>ApproachCommandHandler</c>).</summary>
    public const string FieldNotInSightRefusal = "Field not in sight — issue RFIS first";

    /// <summary>The sim's refusal of a visual approach following traffic not reported in sight.</summary>
    public const string TrafficNotInSightRefusal = "Traffic not in sight — issue RTIS first";

    /// <summary>The altitude from which Cancel IFR is no longer offered (FL180, Class A).</summary>
    private const double ClassAFloorFeet = 18000;

    /// <summary>The badge of the default row pinned above the tabs, which the flyout focuses on open.</summary>
    public const string DefaultBadge = "default";

    private const string ExpectedBadge = "expected";

    private readonly List<RowSource> _pinned;
    private readonly List<List<RowSource>> _tabRows;
    private readonly SituationFlags _flags;

    private ApproachFlyoutModel(
        ApproachVerbSpec spec,
        SituationFlags flags,
        List<RowSource> pinned,
        List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> tabs,
        Options options
    )
    {
        Spec = spec;
        _flags = flags;
        _pinned = pinned;
        _tabRows = [.. tabs.Select(tab => tab.Rows)];
        Tabs = [.. tabs.Select(tab => tab.Tab)];
        SelectedTab = options.SelectedTab;
        OffersForce = options.OffersForce;
        HasBox = options.HasBox;
        CancelIfr = options.CancelIfr;
    }

    /// <summary>The flyout's verb.</summary>
    public ApproachVerbSpec Spec { get; }

    /// <summary>The runway tabs, then Circling; empty when the flyout shows no tabs.</summary>
    public IReadOnlyList<ApproachFlyoutTab> Tabs { get; }

    /// <summary>The tab the flyout opens on; -1 without tabs.</summary>
    public int SelectedTab { get; }

    /// <summary>Whether the title line carries the Force approach toggle: Cleared approach outside solo training.</summary>
    public bool OffersForce { get; }

    /// <summary>Whether the flyout has the Approach box: always but at an untowered field with no instrument approach in Cleared approach.</summary>
    public bool HasBox { get; }

    /// <summary>
    /// The Cancel IFR row, or null when the verb offers none (<see cref="ApproachVerbSpec.OffersCancelIfr"/>) or the aircraft is
    /// not IFR and airborne below FL180.
    /// </summary>
    public ApproachCancelIfr? CancelIfr { get; }

    /// <summary>The flyout's title, its title line's text: the entry's label.</summary>
    public string Title => Spec.Label;

    /// <summary>
    /// The bare verb the Approach box sends with a typed id, switched to the toggle's forced verb under Force approach:
    /// <c>CAPP</c>, <c>CAPPF</c>, <c>EAPP</c>, <c>JAPP</c>.
    /// </summary>
    public string BoxVerb(bool force) => (force && (Spec.ForceToggleVerb is { } forced)) ? forced : Spec.Verb;

    /// <summary>
    /// The model for <paramref name="aircraft"/>'s <paramref name="spec"/> flyout, from the destination's approaches, the
    /// host's runway ends and tower fact, and the menu's solo-training mode.
    /// </summary>
    public static ApproachFlyoutModel Build(ApproachVerbSpec spec, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<ApproachChoice> approaches = ApproachChoices.Choices(aircraft);
        // The sim clears only an IFR aircraft for a visual approach (CVA); Expect approach offers the visuals to any.
        bool visuals = spec.Visuals switch
        {
            ApproachVisualRows.Clearance => IsIfr(aircraft),
            ApproachVisualRows.Expectation => true,
            _ => false,
        };
        RowSource? airport = ((spec.Visuals == ApproachVisualRows.Clearance) && visuals) ? AirportRow(aircraft, host) : null;
        bool airportOnly = (airport is not null) && (approaches.Count == 0);
        List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> tabs = airportOnly ? [] : TabsFor(spec, visuals, aircraft, approaches, host);

        RowSource? defaultRow = DefaultRow(spec, visuals, aircraft, approaches, tabs);
        List<RowSource> pinned = [.. new[] { defaultRow, airport }.OfType<RowSource>()];

        var options = new Options(
            OffersForce: (spec.ForceToggleVerb is not null) && !context.SoloTrainingMode,
            HasBox: !airportOnly,
            CancelIfr: spec.OffersCancelIfr ? CancelIfrFor(aircraft, context, (approaches.Count == 0) && !airportOnly) : null,
            SelectedTab: OpeningTab(aircraft, tabs, defaultRow)
        );
        return new ApproachFlyoutModel(spec, aircraft?.SituationFlags ?? SituationFlags.None, pinned, tabs, options);
    }

    /// <summary>The rows pinned above the tabs, in order: the default row, then the airport-level visual row.</summary>
    public IReadOnlyList<ApproachRow> Pinned(bool force) => [.. _pinned.Select(source => Render(source, force))];

    /// <summary>The rows of the tab at <paramref name="tab"/>, as they show with Force approach on or off.</summary>
    public IReadOnlyList<ApproachRow> Rows(int tab, bool force) => [.. _tabRows[tab].Select(source => Render(source, force))];

    /// <summary>
    /// What the Approach box sends for <paramref name="text"/> on the tab at <paramref name="tab"/>: the command of the first
    /// live row whose id starts with it, else the box's verb with the text upper-cased; null while the text is blank or holds a
    /// space.
    /// </summary>
    public string? BoxCommand(int tab, string text, bool force)
    {
        string typed = text.Trim().ToUpperInvariant();
        if ((typed.Length == 0) || typed.Any(char.IsWhiteSpace))
        {
            return null;
        }

        IEnumerable<ApproachRow> rows = ((tab >= 0) && (tab < _tabRows.Count)) ? Rows(tab, force) : [];
        return rows.FirstOrDefault(row => row.IsEnabled && row.Matches(typed))?.Command ?? $"{BoxVerb(force)} {typed}";
    }

    private ApproachRow Render(RowSource source, bool force)
    {
        bool toggled = force && (Spec.ForceToggleVerb is not null);
        bool forced = Spec.IsForced || toggled;
        ApproachRowSight needs = forced ? ApproachRowSight.None : source.Needs;
        (string? notInSight, string? refusal) = Waiting(needs, _flags);
        return new ApproachRow
        {
            Kind = source.Kind,
            Name = source.Name,
            MatchId = source.MatchId,
            KindText = (source.Kind == ApproachRowKind.Instrument) ? source.KindText : "VIS",
            Command = Command(source, toggled),
            Badge = source.Badge,
            IsForced = forced,
            IsShort = source.IsShort,
            Note = (source.Kind == ApproachRowKind.Airport) ? "pilot picks the runway" : null,
            NotInSight = notInSight,
            Refusal = refusal,
        };
    }

    /// <summary>
    /// The greyed note and refusal for a row still waiting on <paramref name="needs"/> given the aircraft's
    /// <paramref name="flags"/>; nulls once the report is in, or when the row waits for none.
    /// </summary>
    public static (string? NotInSight, string? Refusal) Waiting(ApproachRowSight needs, SituationFlags flags) =>
        needs switch
        {
            ApproachRowSight.Field when !flags.HasFlag(SituationFlags.HasReportedFieldInSight) => ("field not in sight", FieldNotInSightRefusal),
            ApproachRowSight.Traffic when !flags.HasFlag(SituationFlags.HasReportedTrafficInSight) => (
                "traffic not in sight",
                TrafficNotInSightRefusal
            ),
            _ => (null, null),
        };

    /// <summary>
    /// What <paramref name="source"/> sends: an instrument row the verb (the toggle's forced verb when
    /// <paramref name="toggled"/>) with its approach id; a visual or airport row <c>CVA</c> (<c>CVAF</c>) in a clearance, or
    /// <c>EAPP VIS{rwy}</c> in an expectation.
    /// </summary>
    private string Command(RowSource source, bool toggled) =>
        (source.Kind, Spec.Visuals) switch
        {
            (ApproachRowKind.Instrument, _) => $"{BoxVerb(toggled)} {source.Target}",
            (_, ApproachVisualRows.Expectation) => $"{Spec.Verb} {ApproachIds.Visual(source.Target)}",
            _ => $"{(toggled ? "CVAF" : "CVA")} {source.Target}",
        };

    /// <summary>
    /// The runway tabs in <see cref="RunwaysFor"/>'s order, any runway with an approach the host does not list after them in runway
    /// order, then Circling; each runway tab its visual row first, then its approaches.
    /// </summary>
    private static List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> TabsFor(
        ApproachVerbSpec spec,
        bool visuals,
        IMenuAircraft? aircraft,
        List<ApproachChoice> approaches,
        IMenuHost host
    )
    {
        string? expectedVisual = ApproachIds.TryParseVisual(aircraft?.ExpectedApproach, out string? visual) ? visual : null;
        string? expectedId = ApproachChoices.Expected(aircraft, approaches)?.Id;
        List<(string Runway, bool IsShort)> runways = RunwaysFor(aircraft, approaches, host);
        List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> tabs = [];
        foreach ((string runway, bool isShort) in runways)
        {
            bool visualExpected =
                (expectedVisual is not null) && ApproachChoices.SameRunway(RunwayIdentifier.NormalizeDesignator(runway), expectedVisual);
            List<RowSource> rows = visuals ? [VisualRow(spec, runway, isShort, visualExpected ? ExpectedBadge : null)] : [];
            rows.AddRange(
                approaches
                    .Where(approach => ApproachChoices.SameRunway(approach.Runway, runway))
                    .Select(approach => InstrumentRow(approach, approach.FullName, isShort, (approach.Id == expectedId) ? ExpectedBadge : null))
            );
            // Without visual rows, a runway with no instrument approach has nothing to offer and gets no tab.
            if (rows.Count > 0)
            {
                tabs.Add((new ApproachFlyoutTab($"{runway} {rows.Count}", runway), rows));
            }
        }

        List<RowSource> circling =
        [
            .. approaches
                .Where(approach => approach.Runway.Length == 0)
                .Select(approach => InstrumentRow(approach, approach.FullName, false, (approach.Id == expectedId) ? ExpectedBadge : null)),
        ];
        if (circling.Count > 0)
        {
            tabs.Add((new ApproachFlyoutTab($"{CirclingTab} {circling.Count}", CirclingTab), circling));
        }

        return tabs;
    }

    /// <summary>
    /// The destination's runway ends in display form with whether each is short for the type: the host's pattern-entry ends,
    /// the room's arrival ends first in the room's order (<see cref="IMenuHost.GetActiveArrivalEnds"/>), then any runway an
    /// approach names that they leave out, in runway order. Those are never marked short: the host leaves an end out when the
    /// navigation data does not resolve it, so its landing distance is unknown.
    /// </summary>
    private static List<(string Runway, bool IsShort)> RunwaysFor(IMenuAircraft? aircraft, List<ApproachChoice> approaches, IMenuHost host)
    {
        IReadOnlyList<PatternRunwayChoice> ends = aircraft is { Destination.Length: > 0 }
            ? host.GetPatternRunwayChoices(aircraft.Destination, aircraft.BaseAircraftType)
            : [];
        List<string> roomOrder = aircraft is { Destination.Length: > 0 }
            ? [.. host.GetActiveArrivalEnds(aircraft.Destination).Select(RunwayIdentifier.NormalizeDesignator)]
            : [];
        // The room's arrival ends in the room's order, then the rest in the pattern-entry order (the sort is stable).
        int RoomRank(PatternRunwayChoice end)
        {
            int rank = roomOrder.FindIndex(active => string.Equals(active, end.End.Designator, StringComparison.OrdinalIgnoreCase));
            return !end.IsActive ? int.MaxValue
                : (rank >= 0) ? rank
                : roomOrder.Count;
        }

        List<(string Runway, bool IsShort)> runways = [.. ends.OrderBy(RoomRank).Select(end => (end.Designator, !end.IsLandable))];
        IEnumerable<string> unlisted = approaches
            .Select(approach => approach.Runway)
            .Where(runway => (runway.Length > 0) && !runways.Any(listed => ApproachChoices.SameRunway(runway, listed.Runway)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(RunwayDesignatorComparer.Instance);
        runways.AddRange(unlisted.Select(runway => (RunwayIdentifier.ToDisplayDesignator(runway), false)));
        return runways;
    }

    private static RowSource VisualRow(ApproachVerbSpec spec, string runway, bool isShort, string? badge) =>
        new()
        {
            Kind = ApproachRowKind.Visual,
            Name = $"Visual RWY {runway}",
            MatchId = ApproachIds.Visual(runway),
            Target = runway,
            Badge = badge,
            IsShort = isShort,
            Needs = (spec.Visuals == ApproachVisualRows.Clearance) ? ApproachRowSight.Field : ApproachRowSight.None,
        };

    private static RowSource InstrumentRow(ApproachChoice approach, string name, bool isShort, string? badge) =>
        new()
        {
            Kind = ApproachRowKind.Instrument,
            Name = name,
            MatchId = approach.Id,
            Target = approach.Id,
            Badge = badge,
            IsShort = isShort,
            Needs = ApproachRowSight.None,
            KindText = approach.Kind,
        };

    /// <summary>
    /// The airport-level visual row (<c>CVA {airport}</c>, the pilot picking the runway) at an untowered destination the
    /// navigation data has runways for; null at a towered one, or at one without runway data, where the box alone serves.
    /// </summary>
    private static RowSource? AirportRow(IMenuAircraft? aircraft, IMenuHost host)
    {
        if ((aircraft is not { Destination.Length: > 0 }) || host.IsToweredAirport(aircraft.Destination))
        {
            return null;
        }

        string destination = aircraft.Destination;
        return (host.GetPatternRunwayChoices(destination, aircraft.BaseAircraftType).Count == 0)
            ? null
            : new RowSource
            {
                Kind = ApproachRowKind.Airport,
                Name = $"Visual approach to {destination}",
                MatchId = destination,
                Target = destination,
                Badge = null,
                IsShort = false,
                Needs = ApproachRowSight.Field,
            };
    }

    private static bool IsIfr(IMenuAircraft? aircraft) => string.Equals(aircraft?.FlightRules, "IFR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The default row pinned above the tabs: the expected approach, else the expected visual's runway when it has a tab, else
    /// the assigned runway's first approach; null with none. It carries the <c>default</c> badge and the runway's "short".
    /// </summary>
    private static RowSource? DefaultRow(
        ApproachVerbSpec spec,
        bool visuals,
        IMenuAircraft? aircraft,
        List<ApproachChoice> approaches,
        List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> tabs
    )
    {
        if (ApproachChoices.Expected(aircraft, approaches) is { } expected)
        {
            return InstrumentRow(expected, spec.DefaultLabel(expected.ShortName), IsShortRunway(tabs, expected.Runway), DefaultBadge) with
            {
                DefaultOf = expected.Runway,
            };
        }

        if (
            visuals
            && ApproachIds.TryParseVisual(aircraft?.ExpectedApproach, out string? visual)
            && (TabOf(tabs, RunwayIdentifier.NormalizeDesignator(visual)) is { } tab)
        )
        {
            string runway = tabs[tab].Tab.Runway;
            return VisualRow(spec, runway, IsShortRunway(tabs, RunwayIdentifier.NormalizeDesignator(runway)), DefaultBadge) with
            {
                Name = spec.DefaultLabel($"visual {runway}"),
                DefaultOf = RunwayIdentifier.NormalizeDesignator(runway),
            };
        }

        return ApproachChoices.AssignedRunwayApproach(aircraft, approaches) is { } assigned
            ? InstrumentRow(assigned, spec.DefaultLabel(assigned.ShortName), IsShortRunway(tabs, assigned.Runway), DefaultBadge) with
            {
                DefaultOf = assigned.Runway,
            }
            : null;
    }

    /// <summary>The index of the tab of normalized <paramref name="runway"/>, or of Circling for an empty one; null when there is none.</summary>
    private static int? TabOf(List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> tabs, string runway)
    {
        int index = tabs.FindIndex(tab =>
            (runway.Length == 0)
                ? (tab.Tab.Runway == CirclingTab)
                : (tab.Tab.Runway != CirclingTab) && ApproachChoices.SameRunway(RunwayIdentifier.NormalizeDesignator(tab.Tab.Runway), runway)
        );
        return (index >= 0) ? index : null;
    }

    private static bool IsShortRunway(List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> tabs, string runway) =>
        (runway.Length > 0) && (TabOf(tabs, runway) is { } tab) && tabs[tab].Rows[0].IsShort;

    /// <summary>
    /// The default row's tab, else the default runway's (<see cref="MenuCatalog.SmartVisualRunway"/>), else the first; -1
    /// without tabs.
    /// </summary>
    private static int OpeningTab(IMenuAircraft? aircraft, List<(ApproachFlyoutTab Tab, List<RowSource> Rows)> tabs, RowSource? defaultRow)
    {
        if (tabs.Count == 0)
        {
            return -1;
        }

        if ((defaultRow?.DefaultOf is { } defaultRunway) && (TabOf(tabs, defaultRunway) is { } defaultTab))
        {
            return defaultTab;
        }

        string? smart = MenuCatalog.SmartVisualRunway(aircraft);
        return ((smart is not null) && (TabOf(tabs, RunwayIdentifier.NormalizeDesignator(smart)) is { } smartTab)) ? smartTab : 0;
    }

    /// <summary>Cancel IFR for an IFR aircraft airborne below FL180: pinned at the top with no instrument approach, else last.</summary>
    private static ApproachCancelIfr? CancelIfrFor(IMenuAircraft? aircraft, MenuContext context, bool first)
    {
        bool offered = (aircraft is not null) && IsIfr(aircraft) && !aircraft.IsOnGround && (aircraft.AltitudeFeet < ClassAFloorFeet);
        string question =
            $"Cancel IFR for {context.Callsign}? The aircraft becomes VFR: no further approach clearances, and VFR pattern entry applies.";
        return offered ? new ApproachCancelIfr(first ? CancelIfrPlacement.Top : CancelIfrPlacement.Bottom, question) : null;
    }

    /// <summary>The flyout's options beside its rows.</summary>
    private sealed record Options(bool OffersForce, bool HasBox, ApproachCancelIfr? CancelIfr, int SelectedTab);

    /// <summary>A row before Force approach is applied.</summary>
    private sealed record RowSource
    {
        /// <summary>What the row stands for.</summary>
        public required ApproachRowKind Kind { get; init; }

        /// <summary>The row's name.</summary>
        public required string Name { get; init; }

        /// <summary>The id the Approach box matches.</summary>
        public required string MatchId { get; init; }

        /// <summary>What the row sends with: the approach id, the runway in display form, or the airport.</summary>
        public required string Target { get; init; }

        /// <summary>The badge after the name, or null.</summary>
        public required string? Badge { get; init; }

        /// <summary>Whether the runway is short for the type.</summary>
        public required bool IsShort { get; init; }

        /// <summary>The report the row waits for in Cleared approach without Force approach: the field, the traffic, or none.</summary>
        public required ApproachRowSight Needs { get; init; }

        /// <summary>An instrument row's kind column.</summary>
        public string KindText { get; init; } = "";

        /// <summary>For the default row, the normalized runway (empty for circling) whose tab the flyout opens on.</summary>
        public string? DefaultOf { get; init; }
    }
}

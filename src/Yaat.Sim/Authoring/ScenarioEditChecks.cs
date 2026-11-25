using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Authoring;

/// <summary>How much a live-validation finding matters. None blocks an export.</summary>
public enum ScenarioEditIssueSeverity
{
    /// <summary>The scenario will not run as written: a preset that does not parse, a field its start needs, a TAXI that does not route.</summary>
    Error,

    /// <summary>The scenario runs, but not as the author may expect: a changed procedure version, a position no config lists.</summary>
    Warning,

    /// <summary>A check that could not run on this field, for want of its input (a map, a @GATE destination).</summary>
    NotChecked,
}

/// <summary>One live-validation finding, keyed to the field it is shown beside.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="AircraftId">The scenario aircraft's document id (its ULID); null for a scenario-level finding.</param>
/// <param name="Callsign">The aircraft's callsign; null for a scenario-level finding.</param>
/// <param name="Field">The field it belongs to, one of <see cref="ScenarioEditFields"/>.</param>
/// <param name="PresetId">The preset command's id for a preset finding; null otherwise.</param>
/// <param name="Message">The text shown beside the field.</param>
public sealed record ScenarioEditIssue(
    ScenarioEditIssueSeverity Severity,
    string? AircraftId,
    string? Callsign,
    string Field,
    string? PresetId,
    string Message
);

/// <summary>What the live checks read besides the scenario; a null position set or layout source skips its check.</summary>
/// <param name="IsNavDataLoaded">Whether navdata is loaded; when it is not, no check runs and the one finding says so.</param>
/// <param name="PositionIds">
/// The position ids of the ARTCC's own config and its neighbours' configs; null when a config could not be read, which skips
/// the position checks.
/// </param>
/// <param name="Layouts">
/// The airport ground layouts TAXI presets are routed on; null reports the TAXI of each aircraft that starts on the ground as
/// not checked.
/// </param>
public sealed record ScenarioEditInputs(Func<bool> IsNavDataLoaded, IReadOnlySet<string>? PositionIds, IAirportGroundData? Layouts);

/// <summary>The field keys of <see cref="ScenarioEditIssue.Field"/>; a starting condition's member is keyed by its JSON name.</summary>
public static class ScenarioEditFields
{
    /// <summary>A finding about the whole scenario, shown in the window's status bar.</summary>
    public const string Scenario = "scenario";

    /// <summary>The aircraft's callsign.</summary>
    public const string Callsign = "callsign";

    /// <summary>The aircraft's type.</summary>
    public const string AircraftType = "aircraftType";

    /// <summary>The aircraft's airport id.</summary>
    public const string AirportId = "airportId";

    /// <summary>The starting-condition kind.</summary>
    public const string StartKind = "type";

    /// <summary>The starting conditions' parking spot.</summary>
    public const string Parking = "parking";

    /// <summary>The starting conditions' runway.</summary>
    public const string Runway = "runway";

    /// <summary>The starting conditions' fix or fix-radial-distance.</summary>
    public const string Fix = "fix";

    /// <summary>The starting conditions' coordinates.</summary>
    public const string Coordinates = "coordinates";

    /// <summary>The starting conditions' navigation path.</summary>
    public const string NavigationPath = "navigationPath";

    /// <summary>A preset command's text; the finding carries the preset's id.</summary>
    public const string PresetCommand = "presetCommand";

    /// <summary>The auto-track position.</summary>
    public const string AutoTrackPosition = "autoTrack.positionId";

    /// <summary>The scenario's student position.</summary>
    public const string StudentPosition = "studentPositionId";

    /// <summary>The position of the ATC row at <paramref name="index"/> of the scenario's ATC list.</summary>
    /// <param name="index">The row's index into the scenario's ATC list.</param>
    /// <returns>The field key.</returns>
    public static string AtcPosition(int index) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"atc[{index}].positionId");
}

/// <summary>
/// The Scenario Editor's live checks: <see cref="ScenarioValidator"/>'s findings mapped to the aircraft, field and preset they
/// belong to, then the editor's own checks: duplicate callsigns, auto-track without a position, positions no config lists, the
/// fields a starting-condition kind needs, and whether an aircraft that starts on the ground can taxi its first TAXI preset.
/// </summary>
public static class ScenarioEditChecks
{
    /// <summary>The scenario-level finding when navdata is not loaded: nothing else is checked.</summary>
    public const string NavdataNotLoadedText = "Navdata not loaded: the scenario was not checked.";

    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioEditChecks");

    private static readonly GroundCommandHandler.TaxiDispatch PresetTaxiDispatch = new(
        AutoCrossRunwayMode.Off,
        ActiveRunways.Empty,
        IsScenarioScripted: true,
        ResultReachesController: false,
        () => [],
        GroundCommandHandler.DepartureRunwayClearance.Taxi
    );

    /// <summary>Runs every check on <paramref name="scenario"/>, in document order. Never throws on a scenario it can read.</summary>
    /// <param name="scenario">The scenario as the editor holds it; left unchanged.</param>
    /// <param name="inputs">The navdata, position ids and layouts the checks read.</param>
    /// <returns>The findings; only <see cref="NavdataNotLoadedText"/> when navdata is not loaded.</returns>
    public static IReadOnlyList<ScenarioEditIssue> Run(Scenario scenario, ScenarioEditInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(inputs);
        if (!inputs.IsNavDataLoaded())
        {
            return [new ScenarioEditIssue(ScenarioEditIssueSeverity.Warning, null, null, ScenarioEditFields.Scenario, null, NavdataNotLoadedText)];
        }

        var issues = new List<ScenarioEditIssue>();
        foreach (ScenarioAircraft aircraft in scenario.Aircraft)
        {
            AddValidatorFindings(scenario, aircraft, issues);
        }

        AddDuplicateCallsigns(scenario, issues);
        foreach (ScenarioAircraft aircraft in scenario.Aircraft)
        {
            AddAutoTrack(aircraft, inputs.PositionIds, issues);
            AddKindFields(aircraft, scenario.PrimaryAirportId, issues);
        }

        AddScenarioPositions(scenario, inputs.PositionIds, issues);
        AddTaxiPresets(scenario, inputs.Layouts, issues);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Live checks of {ScenarioId}: {Count} findings", scenario.Id, issues.Count);
        }

        return issues;
    }

    // ScenarioValidator over a scenario of this one aircraft, so each finding belongs to it whatever callsign another shares;
    // a parse failure names its preset by its command text, the first of the aircraft's presets with that text not yet named.
    private static void AddValidatorFindings(Scenario scenario, ScenarioAircraft aircraft, List<ScenarioEditIssue> issues)
    {
        var single = new Scenario
        {
            Id = scenario.Id,
            Name = scenario.Name,
            Aircraft = [aircraft],
        };
        ScenarioValidationResult result = ScenarioValidator.Validate(single);
        int next = 0;
        foreach (PresetParseFailure failure in result.Failures)
        {
            int at = aircraft.PresetCommands.FindIndex(next, p => string.Equals(p.Command, failure.Command, StringComparison.Ordinal));
            next = (at >= 0) ? at + 1 : next;
            string? presetId = (at >= 0) ? aircraft.PresetCommands[at].Id : null;
            string message = $"The command does not parse: {failure.Reason ?? "no reason given"}";
            issues.Add(Issue(ScenarioEditIssueSeverity.Error, aircraft, ScenarioEditFields.PresetCommand, presetId, message));
        }

        issues.AddRange(result.ProcedureIssues.Select(p => ProcedureIssueOf(aircraft, p)));
        issues.AddRange(result.TransitionFixSubstitutions.Select(s => TransitionIssueOf(aircraft, s)));
        issues.AddRange(
            result.AircraftTypeMismatches.Select(m =>
                Issue(
                    ScenarioEditIssueSeverity.Warning,
                    aircraft,
                    ScenarioEditFields.AircraftType,
                    null,
                    $"The aircraft type {m.ActualType} differs from the filed type {m.FiledType}"
                )
            )
        );
    }

    private static ScenarioEditIssue ProcedureIssueOf(ScenarioAircraft aircraft, ProcedureIssue issue) =>
        (issue.Kind == ProcedureIssueKind.NotFound)
            ? Issue(
                ScenarioEditIssueSeverity.Error,
                aircraft,
                ScenarioEditFields.NavigationPath,
                null,
                $"The procedure {issue.ProcedureId} is not in the navdata"
            )
            : Issue(
                ScenarioEditIssueSeverity.Warning,
                aircraft,
                ScenarioEditFields.NavigationPath,
                null,
                $"The procedure {issue.ProcedureId} is now {issue.ResolvedId}"
            );

    private static ScenarioEditIssue TransitionIssueOf(ScenarioAircraft aircraft, TransitionFixSubstitution sub)
    {
        string replaced = (sub.NewFix is { } newFix) ? $"{newFix} replaces it" : "no transition fix replaces it";
        string message = $"{sub.OldFix} is not on {sub.ProcedureId}; {replaced}";
        return Issue(ScenarioEditIssueSeverity.Warning, aircraft, ScenarioEditFields.NavigationPath, null, message);
    }

    // Every aircraft of a callsign that more than one aircraft has, compared case-insensitively; a blank callsign is not one.
    private static void AddDuplicateCallsigns(Scenario scenario, List<ScenarioEditIssue> issues)
    {
        IEnumerable<IGrouping<string, ScenarioAircraft>> groups = scenario
            .Aircraft.Where(a => !string.IsNullOrWhiteSpace(a.AircraftId))
            .GroupBy(a => a.AircraftId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Skip(1).Any());
        var duplicated = new HashSet<ScenarioAircraft>(groups.SelectMany(g => g), ReferenceEqualityComparer.Instance);
        foreach (ScenarioAircraft aircraft in scenario.Aircraft.Where(duplicated.Contains))
        {
            int count = scenario.Aircraft.Count(a =>
                string.Equals(a.AircraftId?.Trim(), aircraft.AircraftId.Trim(), StringComparison.OrdinalIgnoreCase)
            );
            string message = $"{count} aircraft have the callsign {aircraft.AircraftId.Trim()}";
            issues.Add(Issue(ScenarioEditIssueSeverity.Error, aircraft, ScenarioEditFields.Callsign, null, message));
        }
    }

    private static void AddAutoTrack(ScenarioAircraft aircraft, IReadOnlySet<string>? positionIds, List<ScenarioEditIssue> issues)
    {
        if (aircraft.AutoTrackConditions is not { } autoTrack)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(autoTrack.PositionId))
        {
            issues.Add(Issue(ScenarioEditIssueSeverity.Error, aircraft, ScenarioEditFields.AutoTrackPosition, null, "Auto-track needs a position"));
        }
        else if (IsMissingPosition(autoTrack.PositionId, positionIds))
        {
            issues.Add(
                Issue(ScenarioEditIssueSeverity.Warning, aircraft, ScenarioEditFields.AutoTrackPosition, null, NotInConfig(autoTrack.PositionId))
            );
        }
    }

    // The members ScenarioLoader needs to place an aircraft of its kind, and the airport it looks the start up at.
    private static void AddKindFields(ScenarioAircraft aircraft, string? primaryAirportId, List<ScenarioEditIssue> issues)
    {
        StartingConditions conditions = aircraft.StartingConditions;
        string kind = conditions.Type ?? "";
        if (!StartingConditionKinds.Kinds.Contains(kind, StringComparer.Ordinal))
        {
            string unknown = $"Unknown starting-condition kind '{kind}'";
            issues.Add(Issue(ScenarioEditIssueSeverity.Error, aircraft, ScenarioEditFields.StartKind, null, unknown));
            return;
        }

        foreach (string member in StartingConditionKinds.RequiredOf(kind).Where(m => IsMissing(conditions, m)))
        {
            issues.Add(Issue(ScenarioEditIssueSeverity.Error, aircraft, member, null, $"A {kind} start needs {MemberLabel(member)}"));
        }

        if (StartAirportId(aircraft, kind, primaryAirportId) is { Length: 0 })
        {
            issues.Add(Issue(ScenarioEditIssueSeverity.Error, aircraft, ScenarioEditFields.AirportId, null, $"A {kind} start needs an airport"));
        }
    }

    private static bool IsMissing(StartingConditions conditions, string member) =>
        member switch
        {
            ScenarioEditFields.Parking => string.IsNullOrEmpty(conditions.Parking),
            ScenarioEditFields.Runway => string.IsNullOrEmpty(conditions.Runway),
            ScenarioEditFields.Fix => string.IsNullOrWhiteSpace(conditions.Fix),
            ScenarioEditFields.Coordinates => conditions.Coordinates is null,
            _ => false,
        };

    private static string MemberLabel(string member) =>
        member switch
        {
            ScenarioEditFields.Parking => "a parking spot",
            ScenarioEditFields.Runway => "a runway",
            ScenarioEditFields.Fix => "a fix",
            ScenarioEditFields.Coordinates => "coordinates",
            _ => member,
        };

    // The airport ScenarioLoader places a start of the kind at, empty when it has none; null for a kind that needs none.
    private static string? StartAirportId(ScenarioAircraft aircraft, string kind, string? primaryAirportId) =>
        kind switch
        {
            StartingConditionKinds.Parking => aircraft.AirportId ?? primaryAirportId ?? aircraft.FlightPlan?.Departure ?? "",
            StartingConditionKinds.OnRunway or StartingConditionKinds.OnFinal => aircraft.AirportId ?? aircraft.FlightPlan?.Departure ?? "",
            _ => null,
        };

    private static void AddScenarioPositions(Scenario scenario, IReadOnlySet<string>? positionIds, List<ScenarioEditIssue> issues)
    {
        if (positionIds is null)
        {
            return;
        }

        if ((!string.IsNullOrWhiteSpace(scenario.StudentPositionId)) && (IsMissingPosition(scenario.StudentPositionId, positionIds)))
        {
            issues.Add(ScenarioIssue(ScenarioEditFields.StudentPosition, NotInConfig(scenario.StudentPositionId)));
        }

        List<ScenarioAtc> atc = scenario.Atc ?? [];
        for (int i = 0; i < atc.Count; i++)
        {
            if ((!string.IsNullOrWhiteSpace(atc[i].PositionId)) && (IsMissingPosition(atc[i].PositionId, positionIds)))
            {
                issues.Add(ScenarioIssue(ScenarioEditFields.AtcPosition(i), NotInConfig(atc[i].PositionId)));
            }
        }
    }

    private static bool IsMissingPosition(string positionId, IReadOnlySet<string>? positionIds) =>
        (positionIds is not null) && (!positionIds.Contains(positionId.Trim()));

    private static string NotInConfig(string positionId) => $"{positionId.Trim()} is not in the ARTCC's config or its neighbours'";

    // Each aircraft's first TAXI preset, routed from where ScenarioLoader places that aircraft alone. A placement or a routing
    // that throws is logged and reported as not checked, so one bad TAXI never hides the other findings.
    private static void AddTaxiPresets(Scenario scenario, IAirportGroundData? layouts, List<ScenarioEditIssue> issues)
    {
        foreach (ScenarioAircraft aircraft in scenario.Aircraft)
        {
            if ((FirstTaxi(aircraft) is { } first) && (CheckTaxi(scenario, first, layouts) is { } issue))
            {
                issues.Add(issue);
            }
        }
    }

    private static ScenarioEditIssue? CheckTaxi(
        Scenario scenario,
        (ScenarioAircraft Aircraft, PresetCommand Preset, TaxiCommand Taxi) first,
        IAirportGroundData? layouts
    )
    {
        try
        {
            return JudgeTaxi(first.Aircraft, first.Preset, first.Taxi, Place(scenario, first.Aircraft, layouts));
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Live checks of {ScenarioId}: checking {Callsign}'s TAXI failed", scenario.Id, first.Aircraft.AircraftId);
            return StartsOnTheGround(first.Aircraft)
                ? NotChecked(first.Aircraft, first.Preset, $"Not checked: the check failed ({ex.Message})")
                : null;
        }
    }

    // The aircraft's first preset that parses to a command list holding a TAXI; null when it has none.
    private static (ScenarioAircraft, PresetCommand, TaxiCommand)? FirstTaxi(ScenarioAircraft aircraft)
    {
        var context = new FixResolutionContext(aircraft.FlightPlan?.Destination, aircraft.FlightPlan?.Departure);
        foreach (PresetCommand preset in aircraft.PresetCommands.Where(p => !string.IsNullOrWhiteSpace(p.Command)))
        {
            ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(preset.Command, aircraft.FlightPlan?.Route, ctx: context);
            if (parsed.Value?.Blocks.SelectMany(b => b.Commands).OfType<TaxiCommand>().FirstOrDefault() is { } taxi)
            {
                return (aircraft, preset, taxi);
            }
        }

        return null;
    }

    // The aircraft as ScenarioLoader places it in a scenario of only itself, so its start is its own; null when the loader drops it.
    private static LoadedAircraft? Place(Scenario scenario, ScenarioAircraft aircraft, IAirportGroundData? layouts)
    {
        var single = new Scenario
        {
            Id = scenario.Id,
            Name = scenario.Name,
            PrimaryAirportId = scenario.PrimaryAirportId,
            PrimaryApproach = scenario.PrimaryApproach,
            Aircraft = [aircraft],
        };
        ScenarioLoadResult result = ScenarioLoader.Load(ScenarioDocument.FromScenario(single).Write(), layouts, new Random(0), DateTime.UtcNow);
        return result.ImmediateAircraft.Concat(result.DelayedAircraft).Concat(result.DeferredAircraft).FirstOrDefault();
    }

    // Null when the TAXI routes, the aircraft starts airborne or the loader did not place it (its kind check says why).
    private static ScenarioEditIssue? JudgeTaxi(ScenarioAircraft aircraft, PresetCommand preset, TaxiCommand taxi, LoadedAircraft? loaded)
    {
        if (loaded is null)
        {
            return null;
        }

        if (loaded.DeferralReason is { } reason)
        {
            return StartsOnTheGround(aircraft) ? NotChecked(aircraft, preset, $"Not checked: the aircraft is not placed ({reason})") : null;
        }

        AircraftState state = loaded.State;
        if (!state.IsOnGround)
        {
            return null;
        }

        if (state.Ground.Layout is not { } layout)
        {
            return NotChecked(aircraft, preset, "Not checked: no airport map");
        }

        if (IsUnresolvedDestination(taxi))
        {
            return NotChecked(aircraft, preset, "Not checked: a @GATE or stand-prefix destination is not routed yet");
        }

        GroundCommandHandler.TaxiRouteResolution resolution = GroundCommandHandler.ResolveTaxiRouteWithFailure(
            state,
            taxi,
            layout,
            PresetTaxiDispatch
        );
        return (resolution.Route is not null)
            ? null
            : Issue(
                ScenarioEditIssueSeverity.Error,
                aircraft,
                ScenarioEditFields.PresetCommand,
                preset.Id,
                $"The TAXI does not route from the aircraft's start: {resolution.Failure?.HumanMessage ?? "no route"}"
            );
    }

    private static bool StartsOnTheGround(ScenarioAircraft aircraft) =>
        aircraft.StartingConditions.Type is StartingConditionKinds.Parking or StartingConditionKinds.OnRunway;

    // A destination ResolveTaxiRouteWithFailure does not resolve, as the clearance's preparation would.
    private static bool IsUnresolvedDestination(TaxiCommand taxi) =>
        (string.Equals(taxi.DestinationParking, GroundCommandHandler.RemarkGateAlias, StringComparison.OrdinalIgnoreCase))
        || (GroundCommandHandler.IsStandPrefix(taxi.DestinationParking));

    private static ScenarioEditIssue NotChecked(ScenarioAircraft aircraft, PresetCommand preset, string message) =>
        Issue(ScenarioEditIssueSeverity.NotChecked, aircraft, ScenarioEditFields.PresetCommand, preset.Id, message);

    private static ScenarioEditIssue ScenarioIssue(string field, string message) =>
        new(ScenarioEditIssueSeverity.Warning, null, null, field, null, message);

    private static ScenarioEditIssue Issue(
        ScenarioEditIssueSeverity severity,
        ScenarioAircraft aircraft,
        string field,
        string? presetId,
        string message
    ) => new(severity, aircraft.Id, aircraft.AircraftId, field, presetId, message);
}

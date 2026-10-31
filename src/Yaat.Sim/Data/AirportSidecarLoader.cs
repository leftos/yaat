using System.Text.Json;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data;

public sealed class AirportSidecarLoadResult
{
    public List<AirportSidecar> Airports { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads the unified per-airport ground sidecars from <c>Data/ARTCCs/{ARTCC}/Airports/*.json</c>.
/// Warn-don't-throw: a malformed file or section adds a warning and is skipped; the rest still load.
/// </summary>
public static class AirportSidecarLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Sanity bound on an authored ADW range — real windows sit within a few miles of the threshold.</summary>
    private const double MaxAdwRangeNm = 15.0;

    /// <summary>
    /// Every <c>{artccsBaseDir}/{ARTCC}/Airports/*.json</c> sidecar in load order: the ordinal order of each file's path
    /// relative to <paramref name="artccsBaseDir"/>, with forward slashes, which is the order <see cref="LoadAll"/> loads
    /// them in and the order the precompute cache keys the same files in. The order is the same on every OS, unlike a
    /// directory listing.
    /// </summary>
    /// <param name="artccsBaseDir">The ARTCCs data directory.</param>
    /// <returns>The sidecar file paths, in load order.</returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="artccsBaseDir"/> does not exist.</exception>
    public static IReadOnlyList<string> SidecarFilesInLoadOrder(string artccsBaseDir)
    {
        if (!Directory.Exists(artccsBaseDir))
        {
            throw new DirectoryNotFoundException($"ARTCCs directory not found: {artccsBaseDir}");
        }

        return
        [
            .. Directory
                .EnumerateDirectories(artccsBaseDir)
                .Select(artccDir => Path.Combine(artccDir, "Airports"))
                .Where(Directory.Exists)
                .SelectMany(airportsDir => Directory.EnumerateFiles(airportsDir, "*.json"))
                .OrderBy(file => Path.GetRelativePath(artccsBaseDir, file).Replace('\\', '/'), StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/Airports/*.json</c> across every ARTCC subdirectory and parses each into an
    /// <see cref="AirportSidecar"/>, in <see cref="SidecarFilesInLoadOrder"/> order.
    /// </summary>
    public static AirportSidecarLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new AirportSidecarLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (string file in SidecarFilesInLoadOrder(artccsBaseDir))
        {
            LoadFile(file, result);
        }

        return result;
    }

    /// <summary>Parses each of <paramref name="filePaths"/> into an <see cref="AirportSidecar"/>, in the order given.</summary>
    /// <param name="filePaths">The sidecar files, as a rule a subset of <see cref="SidecarFilesInLoadOrder"/> in its order.</param>
    /// <returns>The parsed sidecars and the warnings.</returns>
    public static AirportSidecarLoadResult LoadFiles(IEnumerable<string> filePaths)
    {
        var result = new AirportSidecarLoadResult();
        foreach (string file in filePaths)
        {
            LoadFile(file, result);
        }

        return result;
    }

    private static void LoadFile(string filePath, AirportSidecarLoadResult result)
    {
        AirportSidecarFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AirportSidecarFile>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to parse {filePath}: {ex.Message}");
            return;
        }

        if (file is null)
        {
            result.Warnings.Add($"Null result deserializing {filePath}");
            return;
        }

        if (string.IsNullOrWhiteSpace(file.AirportId))
        {
            result.Warnings.Add($"{filePath}: missing airportId, skipping");
            return;
        }

        string airportId = file.AirportId.Trim().ToUpperInvariant();

        result.Airports.Add(
            new AirportSidecar(airportId)
            {
                SourceFile = filePath,
                AvoidTaxiways = ParseAvoidTaxiways(file, filePath, result),
                TaxiRoutes = ParseTaxiRoutes(file, filePath, airportId, result),
                ImplicitConnectors = ParseImplicitConnectors(file, filePath, result),
                CrossingPairs = ParseCrossingPairs(file, filePath, result),
                OneWayEdges = ParseOneWayEdges(file, filePath, result),
                BlockedTurns = ParseBlockedTurns(file, filePath, result),
                Adw = ParseAdw(file, filePath, result),
                ExitDirections = ParseExitDirections(file, filePath, result),
                CargoOperators = ParseOperatorCodes(file.CargoOperators, "cargoOperators", filePath, result),
                GaOperators = ParseOperatorCodes(file.GaOperators, "gaOperators", filePath, result),
                ParkingAreas = ParseParkingAreas(file, filePath, result),
                ExitCapacity = ParseExitCapacity(file, filePath, result),
                MovementAreaTaxiways = ParsePavementClassNames(file.MovementAreaTaxiways, "movementAreaTaxiways", filePath, result),
                NonMovementTaxilanes = ParsePavementClassNames(file.NonMovementTaxilanes, "nonMovementTaxilanes", filePath, result),
                StandDepartureOverrides = ParseStandDepartures(file, filePath, result),
                StandDepartureAreas = ParseStandDepartureAreas(file, filePath, result),
                StandLeadIns = ParseStandLeadIns(file, filePath, result),
                AsdexLandingTaxiways = ParseAsdexLandingTaxiways(file.AsdexLandingTaxiways, filePath, result),
            }
        );
    }

    /// <summary>
    /// The <c>asdexLandingTaxiways</c> list: trimmed, upper-cased, de-duplicated taxiway names. An absent list (null) stays null,
    /// which means any taxiway alerts; an empty list stays an empty set, which means no taxiway-landing alert. A blank name is
    /// warned and skipped.
    /// </summary>
    private static HashSet<string>? ParseAsdexLandingTaxiways(List<string>? names, string filePath, AirportSidecarLoadResult result)
    {
        if (names is null)
        {
            return null;
        }

        var taxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(names[i]))
            {
                result.Warnings.Add($"{filePath}: asdexLandingTaxiways[{i}] missing name, skipping");
                continue;
            }

            taxiways.Add(names[i].Trim().ToUpperInvariant());
        }

        return taxiways;
    }

    /// <summary>
    /// The <c>standLeadIns</c> section, in file order: each stand trimmed, each taxiway trimmed and upper-cased, each
    /// <c>[lon, lat]</c> point read as a <see cref="LatLon"/>. An entry missing its stand or taxiway, or whose point is not
    /// two numbers, is warned and skipped; a second entry for the same stand (case-insensitive) warns and wins.
    /// </summary>
    private static List<StandLeadIn> ParseStandLeadIns(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var leadIns = new List<StandLeadIn>();
        for (int i = 0; i < file.StandLeadIns.Count; i++)
        {
            StandLeadInEntry entry = file.StandLeadIns[i];
            string location = $"{filePath}: standLeadIns[{i}]";
            if (StandLeadInProblem(entry) is { } problem)
            {
                result.Warnings.Add($"{location} ({entry.Stand}): {problem}, skipping");
                continue;
            }

            string stand = entry.Stand!.Trim();
            int existing = leadIns.FindIndex(l => string.Equals(l.Stand, stand, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                result.Warnings.Add($"{location}: duplicate stand {stand} in this file, last entry wins");
                leadIns.RemoveAt(existing);
            }

            leadIns.Add(new StandLeadIn(stand, entry.Taxiway!.Trim().ToUpperInvariant(), new LatLon(entry.Point![1], entry.Point[0]), entry.Notes));
        }

        return leadIns;
    }

    /// <summary>What is wrong with an authored stand lead-in, or null when it is usable.</summary>
    private static string? StandLeadInProblem(StandLeadInEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Stand))
        {
            return "missing stand";
        }

        if (string.IsNullOrWhiteSpace(entry.Taxiway))
        {
            return "missing taxiway";
        }

        return entry.Point is { Length: 2 } ? null : "point must be [lon, lat]";
    }

    /// <summary>The stand-departure names a sidecar may give, quoted, for a warning.</summary>
    private static string StandDepartureNames => string.Join(", ", Enum.GetNames<StandDeparture>().Select(n => $"'{n}'"));

    /// <summary>The stand departure <paramref name="value"/> names, trimmed and case-insensitive, or null when it names none.</summary>
    private static StandDeparture? ParseStandDepartureName(string? value)
    {
        string? name = Enum.GetNames<StandDeparture>().FirstOrDefault(n => string.Equals(n, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Enum.Parse<StandDeparture>(name);
    }

    /// <summary>
    /// The <c>standDeparture</c> section: stand name, trimmed, to <c>PushBack</c>, <c>TaxiOut</c> or <c>Either</c>
    /// (case-insensitive). A blank name or another value is warned and skipped.
    /// </summary>
    private static Dictionary<string, StandDeparture> ParseStandDepartures(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var overrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase);
        foreach ((string stand, string? value) in file.StandDeparture)
        {
            if (string.IsNullOrWhiteSpace(stand))
            {
                result.Warnings.Add($"{filePath}: standDeparture has a blank stand name, skipping");
                continue;
            }

            if (ParseStandDepartureName(value) is not { } departure)
            {
                result.Warnings.Add($"{filePath}: standDeparture[{stand}] must be one of {StandDepartureNames}, got '{value}', skipping");
                continue;
            }

            overrides[stand.Trim()] = departure;
        }

        return overrides;
    }

    /// <summary>
    /// The <c>standDepartureAreas</c> section, in file order: each rule's runway end zero-pad-normalized, its side
    /// <c>left</c> or <c>right</c> and its departure a <see cref="StandDeparture"/> name (both case-insensitive). A rule
    /// missing its runway, or with another side or departure, is warned and skipped.
    /// </summary>
    private static List<StandDepartureArea> ParseStandDepartureAreas(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var areas = new List<StandDepartureArea>();
        for (int i = 0; i < file.StandDepartureAreas.Count; i++)
        {
            StandDepartureAreaEntry entry = file.StandDepartureAreas[i];
            string location = $"{filePath}: standDepartureAreas[{i}]";
            if (string.IsNullOrWhiteSpace(entry.Runway))
            {
                result.Warnings.Add($"{location} missing runway, skipping");
                continue;
            }

            ExitSide? side = entry.Side?.Trim().ToLowerInvariant() switch
            {
                "left" => ExitSide.Left,
                "right" => ExitSide.Right,
                _ => null,
            };
            if (side is null)
            {
                result.Warnings.Add($"{location} ({entry.Runway}): side must be 'left' or 'right', got '{entry.Side}', skipping");
                continue;
            }

            if (ParseStandDepartureName(entry.Departure) is not { } departure)
            {
                result.Warnings.Add(
                    $"{location} ({entry.Runway}): departure must be one of {StandDepartureNames}, got '{entry.Departure}', skipping"
                );
                continue;
            }

            string runway = RunwayIdentifier.NormalizeDesignator(entry.Runway.Trim().ToUpperInvariant());
            areas.Add(new StandDepartureArea(runway, side.Value, departure, entry.Notes));
        }

        return areas;
    }

    /// <summary>Trimmed, upper-cased, de-duplicated names from one pavement-class list; a blank name is warned and skipped.</summary>
    private static List<string> ParsePavementClassNames(
        List<PavementClassEntry> entries,
        string section,
        string filePath,
        AirportSidecarLoadResult result
    )
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(entries[i].Name))
            {
                result.Warnings.Add($"{filePath}: {section}[{i}] missing name, skipping");
                continue;
            }

            string name = entries[i].Name.Trim().ToUpperInvariant();
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// The <c>exitDirections</c> section: each entry's runway end zero-pad-normalized, its side <c>left</c> or <c>right</c>
    /// and its optional arrival class <c>ga</c> or <c>cargo</c> (both case-insensitive). An entry missing its runway, or with
    /// another side or class, is warned and skipped; a second entry for the same runway and class warns and wins.
    /// </summary>
    private static List<ExitDirectionOverride> ParseExitDirections(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var overrides = new List<ExitDirectionOverride>();
        var indexByKey = new Dictionary<(string Runway, ArrivalClass? Class), int>();
        for (int i = 0; i < file.ExitDirections.Count; i++)
        {
            ExitDirectionEntry entry = file.ExitDirections[i];
            string location = $"{filePath}: exitDirections[{i}]";

            if (string.IsNullOrWhiteSpace(entry.Runway))
            {
                result.Warnings.Add($"{location} missing runway, skipping");
                continue;
            }

            ExitSide? side = entry.Side.Trim().ToLowerInvariant() switch
            {
                "left" => ExitSide.Left,
                "right" => ExitSide.Right,
                _ => null,
            };
            if (side is null)
            {
                result.Warnings.Add($"{location} ({entry.Runway}): side must be 'left' or 'right', got '{entry.Side}', skipping");
                continue;
            }

            if (!TryParseArrivalClass(entry.ArrivalClass, out ArrivalClass? arrivalClass))
            {
                result.Warnings.Add($"{location} ({entry.Runway}): arrivalClass must be 'ga' or 'cargo', got '{entry.ArrivalClass}', skipping");
                continue;
            }

            string runway = RunwayIdentifier.NormalizeDesignator(entry.Runway.Trim().ToUpperInvariant());
            var parsed = new ExitDirectionOverride(runway, arrivalClass, side.Value, entry.Notes);
            if (indexByKey.TryGetValue((runway, arrivalClass), out int existing))
            {
                string classText = arrivalClass switch
                {
                    ArrivalClass.GeneralAviation => "ga",
                    ArrivalClass.Cargo => "cargo",
                    _ => "none",
                };
                result.Warnings.Add($"{location}: duplicate runway {runway} (arrivalClass {classText}) in this file, last entry wins");
                overrides[existing] = parsed;
                continue;
            }

            indexByKey[(runway, arrivalClass)] = overrides.Count;
            overrides.Add(parsed);
        }

        return overrides;
    }

    /// <summary>
    /// An authored arrival class: absent or blank is every arrival (null), else <c>ga</c> or <c>cargo</c>; false for another value.
    /// </summary>
    private static bool TryParseArrivalClass(string? value, out ArrivalClass? arrivalClass)
    {
        arrivalClass = null;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "":
                return true;
            case "ga":
                arrivalClass = ArrivalClass.GeneralAviation;
                return true;
            case "cargo":
                arrivalClass = ArrivalClass.Cargo;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// An operator-code section (<c>cargoOperators</c> or <c>gaOperators</c>): trimmed, upper-cased, de-duplicated
    /// three-letter ICAO operator codes. Anything else is warned and skipped.
    /// </summary>
    private static List<string> ParseOperatorCodes(List<string> codes, string propertyName, string filePath, AirportSidecarLoadResult result)
    {
        var operators = new List<string>();
        for (int i = 0; i < codes.Count; i++)
        {
            string code = (codes[i] ?? "").Trim().ToUpperInvariant();
            if ((code.Length != 3) || !code.All(char.IsAsciiLetterUpper))
            {
                result.Warnings.Add($"{filePath}: {propertyName}[{i}] must be a three-letter ICAO operator code, got '{codes[i]}', skipping");
                continue;
            }

            if (!operators.Contains(code))
            {
                operators.Add(code);
            }
        }

        return operators;
    }

    /// <summary>
    /// The <c>parkingAreas</c> section, in file order: each area upper-cased with its whitespace removed, each stand pattern
    /// trimmed and upper-cased. An entry with a blank area, no stands, a blank stand, a <c>*</c> anywhere but at a stand's end,
    /// or a <c>{n}</c> in a stand its area does not carry is warned and skipped.
    /// </summary>
    private static List<ParkingAreaAlias> ParseParkingAreas(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var areas = new List<ParkingAreaAlias>();
        for (int i = 0; i < file.ParkingAreas.Count; i++)
        {
            ParkingAreaEntry entry = file.ParkingAreas[i];
            string area = string.Concat((entry.Area ?? "").Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
            List<string> stands = [.. (entry.Stands ?? []).Select(s => (s ?? "").Trim().ToUpperInvariant())];
            if (ParkingAreaProblem(area, stands) is { } problem)
            {
                result.Warnings.Add($"{filePath}: parkingAreas[{i}] ({entry.Area}): {problem}, skipping");
                continue;
            }

            areas.Add(new ParkingAreaAlias(area, stands, entry.Notes));
        }

        return areas;
    }

    /// <summary>What is wrong with a normalized parking area and its stand patterns, or null when they are usable.</summary>
    private static string? ParkingAreaProblem(string area, List<string> stands)
    {
        if (area.Length == 0)
        {
            return "missing area";
        }

        if ((stands.Count == 0) || stands.Any(s => s.Length == 0))
        {
            return "needs at least one stand and no blank stand";
        }

        if (stands.Any(s => (s.IndexOf('*') >= 0) && (s.IndexOf('*') != s.Length - 1)))
        {
            return "a '*' may only end a stand pattern";
        }

        bool areaHasNumber = area.Contains(ParkingAreaAlias.NumberToken, StringComparison.Ordinal);
        return !areaHasNumber && stands.Any(s => s.Contains(ParkingAreaAlias.NumberToken, StringComparison.Ordinal))
            ? "a stand uses {n} but the area does not"
            : null;
    }

    private static List<ExitCapacityRule> ParseExitCapacity(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var rules = new List<ExitCapacityRule>();
        for (int i = 0; i < file.ExitCapacity.Count; i++)
        {
            ExitCapacityEntry entry = file.ExitCapacity[i];
            string location = $"{filePath}: exitCapacity[{i}]";
            if (ExitCapacityProblem(entry) is { } problem)
            {
                result.Warnings.Add($"{location} ({ExitCapacityLabel(entry)}): {problem}, skipping");
                continue;
            }

            string threshold = entry.CwtThreshold.Trim().ToUpperInvariant();
            if (entry.Crossovers.Count == 0)
            {
                rules.Add(
                    new ExitCapacityRule(
                        Runway: NormalizeExitCapacityRunway(entry.Runway),
                        CrossoverRunway: null,
                        Taxiway: entry.Taxiway.Trim().ToUpperInvariant(),
                        MaxAircraft: entry.MaxAircraft,
                        MaxAircraftAboveCwt: entry.MaxAircraftAboveCwt,
                        CwtThreshold: threshold,
                        Notes: entry.Notes
                    )
                );
                continue;
            }

            string first = NormalizeExitCapacityRunway(entry.Runways[0]);
            string second = NormalizeExitCapacityRunway(entry.Runways[1]);
            foreach (string taxiway in entry.Crossovers)
            {
                rules.Add(
                    new ExitCapacityRule(
                        Runway: first,
                        CrossoverRunway: second,
                        Taxiway: taxiway.Trim().ToUpperInvariant(),
                        MaxAircraft: entry.MaxAircraft,
                        MaxAircraftAboveCwt: entry.MaxAircraftAboveCwt,
                        CwtThreshold: threshold,
                        Notes: entry.Notes
                    )
                );
            }
        }

        return rules;
    }

    private static string NormalizeExitCapacityRunway(string runway) => RunwayIdentifier.NormalizeDesignator(runway.Trim().ToUpperInvariant());

    /// <summary>How a warning names an exit-capacity entry: its runway and taxiway, or its runway pair and crossovers.</summary>
    private static string ExitCapacityLabel(ExitCapacityEntry entry)
    {
        List<string> runways = entry.Runways ?? [];
        List<string> crossovers = entry.Crossovers ?? [];
        return ((entry.Runways is null) || (entry.Crossovers is null) || (runways.Count > 0) || (crossovers.Count > 0))
            ? $"{string.Join("/", runways)} crossovers {string.Join(" ", crossovers)}"
            : $"{entry.Runway} {entry.Taxiway}";
    }

    /// <summary>What is wrong with an authored exit-capacity entry, or null when it is usable.</summary>
    private static string? ExitCapacityProblem(ExitCapacityEntry entry)
    {
        if (ExitCapacityFormProblem(entry) is { } formProblem)
        {
            return formProblem;
        }

        if ((entry.MaxAircraft < 1) || (entry.MaxAircraftAboveCwt < 1))
        {
            return "maxAircraft and maxAircraftAboveCwt must be at least 1";
        }

        if (entry.MaxAircraftAboveCwt > entry.MaxAircraft)
        {
            return "maxAircraftAboveCwt must not exceed maxAircraft";
        }

        string threshold = entry.CwtThreshold.Trim().ToUpperInvariant();
        return ((threshold.Length == 1) && (threshold[0] >= 'A') && (threshold[0] <= 'I')) ? null : "cwtThreshold must be one CWT letter A-I";
    }

    /// <summary>
    /// What is wrong with the form of an exit-capacity entry, or null: either <c>runway</c> and <c>taxiway</c>, or
    /// <c>runways</c> (two distinct runway ends) and <c>crossovers</c> (at least one taxiway, none blank or repeated).
    /// </summary>
    private static string? ExitCapacityFormProblem(ExitCapacityEntry entry)
    {
        // An explicit JSON null replaces the empty-list initializer.
        if ((entry.Runways is null) || (entry.Crossovers is null))
        {
            return "runways and crossovers must be lists, not null";
        }

        bool oneRunwayForm = !string.IsNullOrWhiteSpace(entry.Runway) || !string.IsNullOrWhiteSpace(entry.Taxiway);
        bool crossoversForm = (entry.Runways.Count > 0) || (entry.Crossovers.Count > 0);
        if (oneRunwayForm && crossoversForm)
        {
            return "give either runway and taxiway, or runways and crossovers, not both";
        }

        if (!crossoversForm)
        {
            return (string.IsNullOrWhiteSpace(entry.Runway) || string.IsNullOrWhiteSpace(entry.Taxiway)) ? "requires both runway and taxiway" : null;
        }

        return ExitCapacityCrossoversProblem(entry);
    }

    /// <summary>What is wrong with a crossovers-form exit-capacity entry's runway pair and taxiway list, or null.</summary>
    private static string? ExitCapacityCrossoversProblem(ExitCapacityEntry entry)
    {
        List<string> runways = [.. entry.Runways.Where(r => !string.IsNullOrWhiteSpace(r)).Select(NormalizeExitCapacityRunway).Distinct()];
        if ((entry.Runways.Count != 2) || (runways.Count != 2))
        {
            return "runways must name exactly two distinct runway ends";
        }

        if (entry.Crossovers.Count == 0)
        {
            return "crossovers must list at least one taxiway";
        }

        // A JSON null element deserializes as a null string.
        if (entry.Crossovers.Any(string.IsNullOrWhiteSpace))
        {
            return "crossovers must not list a blank taxiway";
        }

        List<string> taxiways = [.. entry.Crossovers.Select(t => t.Trim().ToUpperInvariant())];
        return (taxiways.Distinct().Count() == taxiways.Count) ? null : "crossovers must not list a taxiway twice";
    }

    private static List<AdwWindow> ParseAdw(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var windows = new List<AdwWindow>();
        for (int i = 0; i < file.Adw.Count; i++)
        {
            AdwEntry entry = file.Adw[i];
            string location = $"{filePath}: adw[{i}]";

            if (string.IsNullOrWhiteSpace(entry.ArrivalRunway) || string.IsNullOrWhiteSpace(entry.DepartureRunway))
            {
                result.Warnings.Add($"{location} requires both arrivalRunway and departureRunway, skipping");
                continue;
            }

            string arrival = RunwayIdentifier.NormalizeDesignator(entry.ArrivalRunway.Trim().ToUpperInvariant());
            string departure = RunwayIdentifier.NormalizeDesignator(entry.DepartureRunway.Trim().ToUpperInvariant());

            // A window is always between two converging runways; naming the same one twice is a typo.
            if (string.Equals(arrival, departure, StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"{location} ({arrival}): arrivalRunway and departureRunway must differ, skipping");
                continue;
            }

            // The window runs from the inner range outbound to the outer range; an inverted or
            // zero-width pair would draw two marks with no window between them.
            if (entry.OuterNm <= entry.InnerNm)
            {
                result.Warnings.Add($"{location} ({arrival}/{departure}): outerNm must exceed innerNm, skipping");
                continue;
            }

            // P/CG: the outer range is the point "on the final approach course" — it is always ahead of
            // the threshold, so a non-positive value would put both ends of the window on the runway.
            if (entry.OuterNm <= 0)
            {
                result.Warnings.Add($"{location} ({arrival}/{departure}): outerNm must be positive (out on final), skipping");
                continue;
            }

            // Anything beyond a normal final is authored data gone wrong, not a real window.
            if (entry.OuterNm > MaxAdwRangeNm || entry.InnerNm < -MaxAdwRangeNm)
            {
                result.Warnings.Add($"{location} ({arrival}/{departure}): ranges must be within ±{MaxAdwRangeNm} nm, skipping");
                continue;
            }

            windows.Add(new AdwWindow(arrival, departure, entry.OuterNm, entry.InnerNm, entry.Notes));
        }

        return windows;
    }

    private static List<BlockedTurn> ParseBlockedTurns(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var turns = new List<BlockedTurn>();
        for (int i = 0; i < file.BlockedTurns.Count; i++)
        {
            BlockedTurnEntry entry = file.BlockedTurns[i];
            if (entry.Path.Count < 3)
            {
                result.Warnings.Add($"{filePath}: blockedTurns[{i}] needs at least 3 path points (an L-shape through the apex), skipping");
                continue;
            }

            List<OneWayPoint>? points = ParseWaypointPath(entry.Path, $"blockedTurns[{i}]", filePath, result);
            if (points is null)
            {
                continue;
            }

            turns.Add(new BlockedTurn(points, entry.Notes));
        }

        return turns;
    }

    private static List<OneWayConstraint> ParseOneWayEdges(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var constraints = new List<OneWayConstraint>();
        for (int i = 0; i < file.OneWayEdges.Count; i++)
        {
            OneWayConstraintEntry entry = file.OneWayEdges[i];
            if (entry.Path.Count < 2)
            {
                result.Warnings.Add($"{filePath}: oneWayEdges[{i}] needs at least 2 path points, skipping");
                continue;
            }

            List<OneWayPoint>? points = ParseWaypointPath(entry.Path, $"oneWayEdges[{i}]", filePath, result);
            if (points is null)
            {
                continue;
            }

            bool blockBoth = ParseBlockMode(entry.Block, filePath, i, result);
            HashSet<WakeTurbulenceData.WakeClass> exempt = ParseExemptWakeClasses(entry.ExemptWakeClasses, filePath, i, result);
            constraints.Add(new OneWayConstraint(points, blockBoth, entry.Notes, exempt));
        }

        return constraints;
    }

    /// <summary>
    /// The wake classes a one-way constraint exempts. An absent or <c>null</c> list exempts none; an unknown name is
    /// skipped with a warning; a list naming every class is kept but warned about, since that constraint never applies.
    /// </summary>
    private static HashSet<WakeTurbulenceData.WakeClass> ParseExemptWakeClasses(
        List<string>? names,
        string filePath,
        int index,
        AirportSidecarLoadResult result
    )
    {
        var exempt = new HashSet<WakeTurbulenceData.WakeClass>();
        foreach (string name in names ?? [])
        {
            if (Enum.TryParse(name, ignoreCase: true, out WakeTurbulenceData.WakeClass wakeClass) && Enum.IsDefined(wakeClass))
            {
                exempt.Add(wakeClass);
                continue;
            }

            result.Warnings.Add(
                $"{filePath}: oneWayEdges[{index}].exemptWakeClasses has unknown wake class \"{name}\" (expected Small, Large, Heavy or Super); ignoring it"
            );
        }

        if (exempt.Count == Enum.GetValues<WakeTurbulenceData.WakeClass>().Length)
        {
            result.Warnings.Add($"{filePath}: oneWayEdges[{index}].exemptWakeClasses exempts every wake class, so the constraint never applies");
        }

        return exempt;
    }

    private static List<OneWayPoint>? ParseWaypointPath(List<OneWayWaypoint> path, string location, string filePath, AirportSidecarLoadResult result)
    {
        var points = new List<OneWayPoint>(path.Count);
        foreach (OneWayWaypoint wp in path)
        {
            if (wp.Point.Length != 2)
            {
                result.Warnings.Add($"{filePath}: {location} point must be [lon, lat], skipping");
                return null;
            }

            string? taxiway = string.IsNullOrWhiteSpace(wp.Taxiway) ? null : wp.Taxiway.Trim().ToUpperInvariant();
            points.Add(new OneWayPoint(Lat: wp.Point[1], Lon: wp.Point[0], Taxiway: taxiway));
        }

        return points;
    }

    private static bool ParseBlockMode(string? block, string filePath, int i, AirportSidecarLoadResult result)
    {
        if (string.IsNullOrWhiteSpace(block) || block.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (block.Equals("both", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        result.Warnings.Add($"{filePath}: oneWayEdges[{i}] unknown block '{block}' (expected 'reverse' or 'both'), defaulting to reverse");
        return false;
    }

    private static List<ImplicitConnectorEntry> ParseImplicitConnectors(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var connectors = new List<ImplicitConnectorEntry>();
        for (int i = 0; i < file.ImplicitConnectors.Count; i++)
        {
            ImplicitConnectorEntry entry = file.ImplicitConnectors[i];
            if (string.IsNullOrWhiteSpace(entry.Connector))
            {
                result.Warnings.Add($"{filePath}: implicitConnectors[{i}] missing connector, skipping");
                continue;
            }

            if (entry.Between.Count != 2 || entry.Between.Any(string.IsNullOrWhiteSpace))
            {
                result.Warnings.Add(
                    $"{filePath}: implicitConnectors[{i}] ({entry.Connector}) requires exactly 2 non-blank 'between' taxiways, skipping"
                );
                continue;
            }

            connectors.Add(
                new ImplicitConnectorEntry
                {
                    Connector = entry.Connector.Trim().ToUpperInvariant(),
                    Between = [.. entry.Between.Select(b => b.Trim().ToUpperInvariant())],
                    Notes = entry.Notes,
                }
            );
        }

        return connectors;
    }

    /// <summary>
    /// The <c>crossingPairs</c> section, in file order. An entry whose taxiways are not two distinct non-blank names, or whose
    /// runway does not name both ends (<c>17R/35L</c>, <see cref="IsRunwayPair"/>), is skipped with a warning.
    /// </summary>
    private static List<CrossingPair> ParseCrossingPairs(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var pairs = new List<CrossingPair>();
        for (int i = 0; i < file.CrossingPairs.Count; i++)
        {
            CrossingPairEntry entry = file.CrossingPairs[i];
            List<string> taxiways = [.. entry.Taxiways.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant())];
            if ((entry.Taxiways.Count != 2) || (taxiways.Count != 2) || (taxiways[0] == taxiways[1]))
            {
                result.Warnings.Add($"{filePath}: crossingPairs[{i}] requires exactly 2 distinct non-blank taxiways, skipping");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Runway))
            {
                result.Warnings.Add($"{filePath}: crossingPairs[{i}] ({taxiways[0]}/{taxiways[1]}) missing runway, skipping");
                continue;
            }

            string runway = entry.Runway.Trim().ToUpperInvariant();
            if (!IsRunwayPair(runway))
            {
                result.Warnings.Add(
                    $"{filePath}: crossingPairs[{i}] ({taxiways[0]}/{taxiways[1]}) runway '{entry.Runway}' must name both ends "
                        + "(e.g. 17R/35L), skipping"
                );
                continue;
            }

            pairs.Add(new CrossingPair(taxiways[0], taxiways[1], runway, entry.Notes));
        }

        return pairs;
    }

    /// <summary>
    /// Whether <paramref name="runway"/> names both ends of a runway, <c>17R/35L</c> or <c>9/27</c>: two designators joined by a slash.
    /// </summary>
    private static bool IsRunwayPair(string runway)
    {
        string[] ends = runway.Split('/');
        return (ends.Length == 2) && ends.All(IsRunwayDesignator);
    }

    private static bool IsRunwayDesignator(string end) =>
        (end.Length is >= 1 and <= 3)
        && end.TakeWhile(char.IsDigit).Count() is var digits and (1 or 2)
        && (digits == end.Length || ((digits == end.Length - 1) && (end[^1] is 'L' or 'C' or 'R')));

    private static List<AvoidTaxiwayEntry> ParseAvoidTaxiways(AirportSidecarFile file, string filePath, AirportSidecarLoadResult result)
    {
        var entries = new List<AvoidTaxiwayEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.AvoidTaxiways.Count; i++)
        {
            AvoidTaxiwayEntry entry = file.AvoidTaxiways[i];
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                result.Warnings.Add($"{filePath}: avoidTaxiways[{i}] missing name, skipping");
                continue;
            }

            string name = entry.Name.Trim().ToUpperInvariant();
            if (!seen.Add(name))
            {
                continue;
            }

            entries.Add(new AvoidTaxiwayEntry { Name = name, Notes = entry.Notes });
        }

        return entries;
    }

    private static List<TaxiRouteDefinition> ParseTaxiRoutes(
        AirportSidecarFile file,
        string filePath,
        string airportId,
        AirportSidecarLoadResult result
    )
    {
        var routes = new List<TaxiRouteDefinition>();
        for (int i = 0; i < file.TaxiRoutes.Count; i++)
        {
            TaxiRouteDefinition def = file.TaxiRoutes[i];
            string location = $"{filePath}: taxiRoutes[{i}]";

            if (string.IsNullOrWhiteSpace(def.Name))
            {
                result.Warnings.Add($"{location} missing name, skipping");
                continue;
            }

            if (def.GetPathTokens().Count == 0)
            {
                result.Warnings.Add($"{location} ({def.Name}): empty path, skipping");
                continue;
            }

            int destinationCount =
                (string.IsNullOrWhiteSpace(def.DestinationRunway) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(def.DestinationParking) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(def.DestinationSpot) ? 0 : 1);

            if (destinationCount > 1)
            {
                result.Warnings.Add(
                    $"{location} ({def.Name}): conflicting destinations (set at most one of destinationRunway/destinationParking/destinationSpot), skipping"
                );
                continue;
            }

            def.AirportId = airportId;
            routes.Add(def);
        }

        return routes;
    }
}

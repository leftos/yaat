using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;

namespace Yaat.SpeechSandbox;

/// <summary>
/// <c>--lexicon-inputs &lt;ICAO&gt; &lt;out.json&gt;</c>: dumps the names an airport's speech lexicon covers (fixes within 40 nm and on
/// its procedures, SIDs and STARs, layout gates and spots with a letter, named ground places and VFR landmarks), each with the
/// spoken forms already registered, for <c>tools/generate-speech-lexicon.py</c> to propose new forms from.
/// </summary>
internal static partial class LexiconInputsRunner
{
    private const double FixRadiusNm = 40.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Writes the airport's lexicon inputs; 0 on success, 2 on a usage or navdata error.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --lexicon-inputs <ICAO> <out.json>");
            return 2;
        }

        if (!SandboxNavData.TryInitialize(out string? navDataError))
        {
            Console.Error.WriteLine($"FATAL: {navDataError}");
            return 2;
        }

        string airport = args[0].Trim().ToUpperInvariant();
        NavigationDatabase nav = NavigationDatabase.Instance;
        if (nav.GetAirportPosition(airport) is not { } position)
        {
            Console.Error.WriteLine($"FATAL: airport {airport} has no position in the navdata.");
            return 2;
        }

        if (OuroborosRunner.TryFindRepoRoot() is not { } repo)
        {
            Console.Error.WriteLine("FATAL: must run inside a yaat checkout (needs src/Yaat.Sim/Data/ARTCCs and tests/Yaat.Sim.Tests/TestData).");
            return 2;
        }

        AirportGroundLayout? layout = LoadLayout(repo, airport);
        var inputs = new LexiconInputs(
            airport,
            Fixes(nav, airport, new LatLon(position.Lat, position.Lon)),
            Procedures(nav, airport),
            LayoutNames(layout is null ? [] : GroundLayoutSpeechNames.GateNames(layout), name => nav.GetGatePronunciations(airport, name)),
            LayoutNames(layout is null ? [] : GroundLayoutSpeechNames.SpotNames(layout), name => nav.GetSpotPronunciations(airport, name)),
            [.. nav.GetGroundPlaces(airport).Select(place => new LexiconPlace(place.Name, place.Pronunciations, place.Stands))],
            LoadLandmarks(repo, airport)
        );

        string outPath = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(inputs, JsonOptions) + "\n", new UTF8Encoding(false));
        Console.WriteLine(
            $"{airport}: {inputs.Fixes.Count} fixes, {inputs.Procedures.Count} procedures, {inputs.Gates.Count} gates, "
                + $"{inputs.Spots.Count} spots, {inputs.Places.Count} places, {inputs.Landmarks.Count} landmarks -> {outPath}"
        );
        return 0;
    }

    private static List<LexiconFix> Fixes(NavigationDatabase nav, string airport, LatLon airportPosition)
    {
        SortedDictionary<string, SortedSet<string>> onProcedures = ProcedureFixes(nav, airport);
        var ids = new SortedSet<string>(onProcedures.Keys, StringComparer.Ordinal);
        foreach (NearbyFix near in nav.FixesWithin(airportPosition, FixRadiusNm))
        {
            ids.Add(near.Name.ToUpperInvariant());
        }

        string faa = AirportLayoutDownloader.ToFaaCode(airport);
        List<LexiconFix> fixes = [];
        foreach (string id in ids)
        {
            if (id.Equals(airport, StringComparison.Ordinal) || id.Equals(faa, StringComparison.Ordinal) || RunwayDesignator().IsMatch(id))
            {
                continue;
            }

            (double Lat, double Lon)? fixPosition = nav.GetFixPosition(id);
            double? distanceNm = fixPosition is { } p ? Math.Round(GeoMath.DistanceNm(airportPosition, new LatLon(p.Lat, p.Lon)), 1) : null;
            fixes.Add(
                new LexiconFix(
                    id,
                    distanceNm,
                    onProcedures.TryGetValue(id, out SortedSet<string>? procedures) ? [.. procedures] : [],
                    nav.GetFixPronunciations(id),
                    nav.GetFixDisplayName(id),
                    nav.GetNavaidName(id),
                    PhraseologyVerbalizer.SpellFix(id)
                )
            );
        }

        return fixes;
    }

    /// <summary>Every fix named by a leg of the airport's SIDs, STARs and approaches, with the ids of the procedures naming it.</summary>
    private static SortedDictionary<string, SortedSet<string>> ProcedureFixes(NavigationDatabase nav, string airport)
    {
        var byFix = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (CifpSidProcedure sid in AllSids(nav, airport))
        {
            AddLegs(byFix, sid.ProcedureId, TransitionLegs(sid.CommonLegs, sid.RunwayTransitions, sid.EnrouteTransitions));
        }

        foreach (CifpStarProcedure star in AllStars(nav, airport))
        {
            AddLegs(byFix, star.ProcedureId, TransitionLegs(star.CommonLegs, star.EnrouteTransitions, star.RunwayTransitions));
        }

        foreach (CifpApproachProcedure approach in nav.GetApproaches(airport))
        {
            IEnumerable<CifpLeg> legs = TransitionLegs(approach.CommonLegs, approach.Transitions).Concat(approach.MissedApproachLegs);
            if (approach.HoldInLieuLeg is { } holdInLieu)
            {
                legs = legs.Append(holdInLieu);
            }

            AddLegs(byFix, approach.ApproachId, legs);
        }

        return byFix;
    }

    private static IEnumerable<CifpLeg> TransitionLegs(
        IReadOnlyList<CifpLeg> common,
        params IReadOnlyDictionary<string, CifpTransition>[] transitions
    ) => common.Concat(transitions.SelectMany(group => group.Values).SelectMany(transition => transition.Legs));

    private static void AddLegs(SortedDictionary<string, SortedSet<string>> byFix, string procedureId, IEnumerable<CifpLeg> legs)
    {
        foreach (CifpLeg leg in legs)
        {
            if (string.IsNullOrWhiteSpace(leg.FixIdentifier))
            {
                continue;
            }

            string fix = leg.FixIdentifier.Trim().ToUpperInvariant();
            if (!byFix.TryGetValue(fix, out SortedSet<string>? procedures))
            {
                procedures = new SortedSet<string>(StringComparer.Ordinal);
                byFix[fix] = procedures;
            }

            procedures.Add(procedureId);
        }
    }

    private static IEnumerable<CifpSidProcedure> AllSids(NavigationDatabase nav, string airport) =>
        nav.GetSids(airport).Concat(nav.GetArtccFragmentSids(airport));

    private static IEnumerable<CifpStarProcedure> AllStars(NavigationDatabase nav, string airport) =>
        nav.GetStars(airport).Concat(nav.GetArtccFragmentStars(airport));

    private static List<LexiconProcedure> Procedures(NavigationDatabase nav, string airport)
    {
        IEnumerable<LexiconProcedure> sids = AllSids(nav, airport)
            .Select(sid => sid.ProcedureId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(id => Procedure(nav, id, "sid"));
        IEnumerable<LexiconProcedure> stars = AllStars(nav, airport)
            .Select(star => star.ProcedureId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(id => Procedure(nav, id, "star"));
        return [.. sids, .. stars];
    }

    private static LexiconProcedure Procedure(NavigationDatabase nav, string id, string kind)
    {
        string stripped = NavigationDatabase.StripTrailingDigits(id);
        string baseName = stripped.Length > 0 ? stripped : id;
        return new LexiconProcedure(id, kind, baseName, nav.GetProcedurePronunciations(baseName), PhraseologyVerbalizer.ProcedureName(id).Tts);
    }

    private static List<LexiconLayoutName> LayoutNames(IReadOnlyList<string> names, Func<string, IReadOnlyList<string>> existing) =>
        [.. names.Where(name => name.Any(char.IsLetter)).Select(name => new LexiconLayoutName(name, existing(name)))];

    private static AirportGroundLayout? LoadLayout(string repo, string airport)
    {
        string faa = AirportLayoutDownloader.ToFaaCode(airport).ToLowerInvariant();
        string path = Path.Combine(repo, "tests", "Yaat.Sim.Tests", "TestData", $"{faa}.geojson");
        return File.Exists(path) ? AirportLayoutFile.Load(path, airport) : null;
    }

    /// <summary>The airport's landmarks from every ARTCC's <c>CustomFixes/&lt;faa&gt;-landmarks.json</c>, in ordinal path order.</summary>
    private static List<LexiconLandmark> LoadLandmarks(string repo, string airport)
    {
        string faa = AirportLayoutDownloader.ToFaaCode(airport).ToLowerInvariant();
        string artccs = Path.Combine(repo, "src", "Yaat.Sim", "Data", "ARTCCs");
        IEnumerable<string> paths = Directory
            .EnumerateDirectories(artccs)
            .Select(artcc => Path.Combine(artcc, "CustomFixes", $"{faa}-landmarks.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal);
        return [.. paths.SelectMany(LandmarksIn)];
    }

    private static IEnumerable<LexiconLandmark> LandmarksIn(string path)
    {
        List<CustomFixDefinition> definitions =
            JsonSerializer.Deserialize<List<CustomFixDefinition>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{path} is empty or not a JSON array of custom fixes.");
        return
        [
            .. definitions
                .Where(definition => definition.Aliases.Count > 0)
                .Select(definition => new LexiconLandmark(
                    definition.Aliases[0],
                    definition.Name,
                    definition.SpokenPatterns ?? [],
                    definition.Lat,
                    definition.Lon
                )),
        ];
    }

    [GeneratedRegex("^RW[0-9]{2}[LRC]?$")]
    private static partial Regex RunwayDesignator();

    private sealed record LexiconInputs(
        string Airport,
        IReadOnlyList<LexiconFix> Fixes,
        IReadOnlyList<LexiconProcedure> Procedures,
        IReadOnlyList<LexiconLayoutName> Gates,
        IReadOnlyList<LexiconLayoutName> Spots,
        IReadOnlyList<LexiconPlace> Places,
        IReadOnlyList<LexiconLandmark> Landmarks
    );

    private sealed record LexiconFix(
        string Id,
        double? DistanceNm,
        IReadOnlyList<string> OnProcedures,
        IReadOnlyList<string> ExistingPronunciations,
        string? DisplayName,
        string? NavaidName,
        string SpellFix
    );

    private sealed record LexiconProcedure(string Id, string Kind, string BaseName, IReadOnlyList<string> ExistingPronunciations, string Tts);

    private sealed record LexiconLayoutName(string Name, IReadOnlyList<string> ExistingPronunciations);

    private sealed record LexiconPlace(string Name, IReadOnlyList<string> Pronunciations, IReadOnlyList<string> Stands);

    private sealed record LexiconLandmark(string Alias, string Name, IReadOnlyList<string> SpokenPatterns, double? Lat, double? Lon);
}

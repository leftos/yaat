using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The destination's approaches as the approach flyout (<see cref="ApproachFlyoutModel"/>) lists them: each one's kind,
/// variant and runway read from the CIFP, in flyout order, and the approach a flyout defaults to (the expected one, else the
/// assigned runway's first).
/// </summary>
internal static class ApproachChoices
{
    /// <summary>
    /// The assigned runway's first approach among <paramref name="choices"/> in flyout order (its ILS when it has one); null
    /// without an assigned runway, or when it has no approach.
    /// </summary>
    internal static ApproachChoice? AssignedRunwayApproach(IMenuAircraft? aircraft, List<ApproachChoice> choices)
    {
        string? assigned = aircraft?.AssignedRunway;
        return string.IsNullOrEmpty(assigned) ? null : choices.FirstOrDefault(choice => SameRunway(choice.Runway, assigned));
    }

    /// <summary>
    /// The expected approach among <paramref name="choices"/>, its id resolved from the controller's shorthand
    /// (<c>R28L</c> for <c>R28LY</c>) as the sim resolves it; null when none is expected or it is not the destination's.
    /// </summary>
    internal static ApproachChoice? Expected(IMenuAircraft? aircraft, List<ApproachChoice> choices)
    {
        if (aircraft is not { Destination.Length: > 0, ExpectedApproach: { Length: > 0 } expected })
        {
            return null;
        }

        string id = NavigationDatabase.Instance.ResolveApproachId(aircraft.Destination, expected) ?? expected;
        return choices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a normalized approach runway is the runway <paramref name="designator"/> names; never for a circling approach.</summary>
    internal static bool SameRunway(string runway, string designator) =>
        (runway.Length > 0) && string.Equals(runway, RunwayIdentifier.NormalizeDesignator(designator), StringComparison.OrdinalIgnoreCase);

    /// <summary>The destination's approaches in flyout order: by kind (<see cref="ApproachChoice.KindRank"/>), then by id.</summary>
    internal static List<ApproachChoice> Choices(IMenuAircraft? aircraft)
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

    /// <summary>An approach that names no runway, as the flyout shows it (its runway is empty).</summary>
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
    /// A runway approach's kind by its ARINC 424 type code: the short kind the flyout names it by, and the kind as the
    /// published procedure name spells it (<c>RNAV (GPS)</c>, <c>RNAV (RNP)</c>).
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

    /// <summary>A destination approach as the flyout shows it, its runway normalized (empty for a circling approach).</summary>
    internal sealed record ApproachChoice(string Id, string Kind, string FullKind, string Variant, string Runway)
    {
        /// <summary>The kind's place in the flyout: ILS, LOC, RNAV, RNP, then every other kind alphabetically.</summary>
        public int KindRank =>
            Kind switch
            {
                "ILS" => 0,
                "LOC" => 1,
                "RNAV" => 2,
                "RNP" => 3,
                _ => 4,
            };

        /// <summary>The kind and variant: <c>ILS</c>, <c>RNP Z</c>, a circling <c>VOR/DME-A</c>.</summary>
        public string ShortKind =>
            (Variant.Length == 0) ? Kind
            : (Runway.Length > 0) ? $"{Kind} {Variant}"
            : $"{Kind}-{Variant}";

        /// <summary>
        /// The short name a default row shows: <c>ILS 30</c>, <c>RNAV Y 28L</c>. An RNAV (RNP) approach is named RNAV, as
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
}

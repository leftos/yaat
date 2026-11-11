using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Phases;

/// <summary>A class of arrival an airport sidecar's <c>exitDirections</c> entry may give its own default exit side.</summary>
public enum ArrivalClass
{
    /// <summary>
    /// An arrival the airport counts as general aviation for its exit side: a registration written without its hyphen
    /// (<c>N377L</c>, <c>CFABC</c>), or an airline-style callsign whose operator the airport's sidecar lists in
    /// <c>gaOperators</c> (a business-jet operator such as <c>EJA791</c> or <c>XOJ712</c>).
    /// </summary>
    GeneralAviation,

    /// <summary>An airline-style callsign whose operator the airport's sidecar lists in <c>cargoOperators</c>.</summary>
    Cargo,
}

/// <summary>
/// The default exit (turn-off) side of one arrival, for when the controller has given none: toward where it parks. In order,
/// <list type="number">
/// <item>the side of the arrival runway the parking its flight-plan remarks name lies on (<see cref="FlightPlanParking"/>): a
/// stand id resolved against the airport layout, or an area name through the sidecar's <c>parkingAreas</c>, measured from the
/// runway's centreline;</item>
/// <item>the sidecar's <c>exitDirections</c> entry for the arrival's class (<see cref="ArrivalClass"/>) on that runway end;</item>
/// <item>the runway end's own default, <see cref="AirportGroundLayout.InferPreferredExitSide(string, TrueHeading, AirportSidecarCatalog)"/>.</item>
/// </list>
/// A function of the callsign, the remarks, the runway and the airport data only, so the final-approach forecast, the landing
/// and the runway exit resolve the same side, and a restore re-derives it.
/// </summary>
public static class ArrivalExitSide
{
    /// <summary>
    /// How close to the runway's extended centreline (beyond its ends included) a remark's parking may lie and still give no
    /// side: within it, the parking is neither clearly left nor right, and the class rule decides.
    /// </summary>
    public const double ParkingDeadBandFt = 250.0;

    private static readonly ILogger Log = SimLog.CreateLogger("ArrivalExitSide");

    /// <summary>
    /// The arrival's default exit side on <paramref name="runway"/>, reading the airport sidecars from the global
    /// <see cref="NavigationDatabase"/>. See the overload taking an <see cref="AirportSidecarCatalog"/>.
    /// </summary>
    public static ExitSide? Resolve(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout layout, TrueHeading inferenceHeading) =>
        Resolve(aircraft, runway, layout, inferenceHeading, NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty);

    /// <summary>
    /// The arrival's default exit side on <paramref name="runway"/>: its parking remark's side, else its class's sidecar side,
    /// else the runway end's default, inferred along <paramref name="inferenceHeading"/>. Null when none of them gives one.
    /// </summary>
    public static ExitSide? Resolve(
        AircraftState aircraft,
        RunwayInfo runway,
        AirportGroundLayout layout,
        TrueHeading inferenceHeading,
        AirportSidecarCatalog sidecars
    ) => ForArrival(aircraft, runway, layout, sidecars) ?? layout.InferPreferredExitSide(runway.Designator, inferenceHeading, sidecars);

    /// <summary>
    /// The side this arrival's own data gives — its parking remark, else its class's sidecar entry — or null when neither does
    /// and the runway end's default applies.
    /// </summary>
    public static ExitSide? ForArrival(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout layout, AirportSidecarCatalog sidecars)
    {
        if (ParkingSide(aircraft, runway, layout, sidecars) is { } parked)
        {
            return parked;
        }

        if (
            Classify(aircraft.Callsign, sidecars.GetCargoOperators(layout.AirportId), sidecars.GetGaOperators(layout.AirportId))
            is not { } arrivalClass
        )
        {
            return null;
        }

        ExitSide? side = sidecars.GetExitDirection(layout.AirportId, runway.Designator, arrivalClass);
        if (side is not null)
        {
            Log.LogDebug(
                "{Callsign}: {Class} arrival on {Runway} exits {Side} by the sidecar",
                aircraft.Callsign,
                arrivalClass,
                runway.Designator,
                side
            );
        }

        return side;
    }

    /// <summary>
    /// The arrival's class: <see cref="ArrivalClass.Cargo"/> for an airline-style callsign whose operator is in
    /// <paramref name="cargoOperators"/> (checked first, so it wins over <paramref name="gaOperators"/>);
    /// <see cref="ArrivalClass.GeneralAviation"/> for a registration (<see cref="IsRegistration"/>) or an airline-style
    /// callsign whose operator is in <paramref name="gaOperators"/>; null otherwise, including an airline the airport lists
    /// on neither.
    /// </summary>
    public static ArrivalClass? Classify(string callsign, IReadOnlySet<string> cargoOperators, IReadOnlySet<string> gaOperators)
    {
        if (ArrivalParkingPicker.IsAirlineStyleCallsign(callsign))
        {
            string operatorCode = callsign[..3];
            if (cargoOperators.Contains(operatorCode))
            {
                return ArrivalClass.Cargo;
            }

            return gaOperators.Contains(operatorCode) ? ArrivalClass.GeneralAviation : null;
        }

        return IsRegistration(callsign) ? ArrivalClass.GeneralAviation : null;
    }

    /// <summary>
    /// Whether a callsign is a registration written without its hyphen: a US N-number (<c>N</c>, a digit 1-9, then up to
    /// four more upper-case letters or digits: <c>N377L</c>, <c>N12345</c>) or a non-US registration of four to six
    /// upper-case letters (<c>CFABC</c>, <c>GABCD</c>).
    /// </summary>
    private static bool IsRegistration(string callsign) => IsUsRegistration(callsign) || IsForeignRegistration(callsign);

    /// <summary>A US N-number: <c>N</c>, a digit 1-9, then up to four upper-case letters or digits.</summary>
    private static bool IsUsRegistration(string callsign)
    {
        if ((callsign.Length is < 2 or > 6) || (callsign[0] != 'N') || (callsign[1] is < '1' or > '9'))
        {
            return false;
        }

        for (int i = 2; i < callsign.Length; i++)
        {
            if (!char.IsAsciiLetterUpper(callsign[i]) && !char.IsAsciiDigit(callsign[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A non-US registration written without its hyphen: four to six upper-case letters.</summary>
    private static bool IsForeignRegistration(string callsign) => (callsign.Length is >= 4 and <= 6) && callsign.All(char.IsAsciiLetterUpper);

    /// <summary>
    /// The side of <paramref name="runway"/>'s centreline (its threshold along its true heading) the parking the arrival's
    /// remarks name lies on: the centroid of the stands it resolves to. Null when the remarks name no parking, the layout
    /// has none of it, or it lies within <see cref="ParkingDeadBandFt"/> of the extended centreline.
    /// </summary>
    private static ExitSide? ParkingSide(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout layout, AirportSidecarCatalog sidecars)
    {
        if (FlightPlanParking.Parse(aircraft.FlightPlan.Remarks) is not { } remark)
        {
            return null;
        }

        List<LatLon> stands = StandPositions(remark, layout, sidecars.GetParkingAreas(layout.AirportId));
        if (stands.Count == 0)
        {
            Log.LogDebug(
                "{Callsign}: parking remark {Area}/{Stand} names no stand of {Airport}",
                aircraft.Callsign,
                remark.Area,
                remark.Stand,
                layout.AirportId
            );
            return null;
        }

        var centroid = new LatLon(stands.Average(p => p.Lat), stands.Average(p => p.Lon));
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double crossTrackFt = GeoMath.SignedCrossTrackDistanceNm(centroid, threshold, runway.TrueHeading) * GeoMath.FeetPerNm;
        ExitSide? side =
            Math.Abs(crossTrackFt) <= ParkingDeadBandFt ? null
            : crossTrackFt > 0 ? ExitSide.Right
            : ExitSide.Left;
        Log.LogDebug(
            "{Callsign}: parking remark {Area}/{Stand} lies {CrossTrackFt:F0} ft right of {Runway}'s centreline ({Count} stands): {Side}",
            aircraft.Callsign,
            remark.Area,
            remark.Stand,
            crossTrackFt,
            runway.Designator,
            stands.Count,
            side?.ToString() ?? "no side"
        );
        return side;
    }

    /// <summary>
    /// The positions of the stands a remark names: its stand id when the layout has a parking spot of that name, else every
    /// parking spot the first matching <paramref name="areas"/> entry covers. Empty when neither resolves.
    /// </summary>
    private static List<LatLon> StandPositions(ParkingRemark remark, AirportGroundLayout layout, IReadOnlyList<ParkingAreaAlias> areas)
    {
        if ((remark.Stand is { } stand) && (layout.FindParkingByName(stand) is { } node))
        {
            return [node.Position];
        }

        if ((remark.Area is not { } area) || (AreaStands(area, layout, areas) is not { } match))
        {
            return [];
        }

        return [.. match.Stands.Select(s => s.Node.Position)];
    }

    /// <summary>
    /// The first <paramref name="areas"/> entry a remark's parking area matches (inner whitespace ignored, <c>{n}</c> standing
    /// for a run of digits) and the layout's parking spots its stand patterns cover, in layout order; the stands are empty
    /// when the entry covers none. Null when no entry matches the area.
    /// </summary>
    /// <param name="area">The area as the remark names it, upper-cased (<see cref="ParkingRemark.Area"/>).</param>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="areas">The airport's sidecar <c>parkingAreas</c>.</param>
    /// <returns>The matching entry and its stands, each with its name, or null.</returns>
    public static (ParkingAreaAlias Alias, List<(GroundNode Node, string Name)> Stands)? AreaStands(
        string area,
        AirportGroundLayout layout,
        IReadOnlyList<ParkingAreaAlias> areas
    )
    {
        string compactArea = string.Concat(area.Where(c => !char.IsWhiteSpace(c)));
        foreach (ParkingAreaAlias alias in areas)
        {
            if (MatchArea(alias.Area, compactArea) is not { } number)
            {
                continue;
            }

            List<string> patterns = [.. alias.Stands.Select(p => p.Replace(ParkingAreaAlias.NumberToken, number, StringComparison.Ordinal))];
            return (alias, CoveredStands(layout, patterns));
        }

        return null;
    }

    /// <summary>The layout's named parking spots a stand pattern covers, each with its name, in layout order.</summary>
    private static List<(GroundNode Node, string Name)> CoveredStands(AirportGroundLayout layout, List<string> patterns)
    {
        var stands = new List<(GroundNode Node, string Name)>();
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if ((node is { Type: GroundNodeType.Parking, Name: { } name }) && patterns.Any(p => MatchStand(p, name)))
            {
                stands.Add((node, name));
            }
        }

        return stands;
    }

    /// <summary>
    /// Whether a normalized area pattern matches a remark's compacted, upper-cased area: equal, or equal with
    /// <see cref="ParkingAreaAlias.NumberToken"/> standing for a run of digits. The digits matched (empty without the token), or null for no match.
    /// </summary>
    private static string? MatchArea(string pattern, string area)
    {
        int token = pattern.IndexOf(ParkingAreaAlias.NumberToken, StringComparison.Ordinal);
        if (token < 0)
        {
            return string.Equals(pattern, area, StringComparison.Ordinal) ? "" : null;
        }

        string prefix = pattern[..token];
        string suffix = pattern[(token + ParkingAreaAlias.NumberToken.Length)..];
        if (
            (area.Length <= prefix.Length + suffix.Length)
            || !area.StartsWith(prefix, StringComparison.Ordinal)
            || !area.EndsWith(suffix, StringComparison.Ordinal)
        )
        {
            return null;
        }

        string number = area[prefix.Length..^suffix.Length];
        return number.All(char.IsAsciiDigit) ? number : null;
    }

    /// <summary>A stand pattern against a layout stand name, case-insensitive: equal, or a prefix when the pattern ends in <c>*</c>.</summary>
    private static bool MatchStand(string pattern, string name) =>
        pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
}

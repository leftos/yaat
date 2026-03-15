using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Creates <see cref="NavigationDatabase"/> instances pre-populated with test data.
/// Convenience wrappers around NavigationDatabase.ForTesting() for common test scenarios.
/// </summary>
internal static class TestNavDbFactory
{
    /// <summary>Creates a NavigationDatabase with the given fix positions and runways.</summary>
    internal static NavigationDatabase Make(
        IReadOnlyDictionary<string, (double Lat, double Lon)>? fixes = null,
        IReadOnlyList<RunwayInfo>? runways = null,
        IReadOnlyDictionary<string, double>? elevations = null
    )
    {
        return NavigationDatabase.ForTesting(fixes, runways, null, elevations);
    }

    /// <summary>Creates a NavigationDatabase with the given airport elevations (for AGL altitude resolution).</summary>
    internal static NavigationDatabase WithElevations(params (string Code, double ElevationFt)[] elevations)
    {
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, elev) in elevations)
        {
            dict[code] = elev;
        }

        return NavigationDatabase.ForTesting(null, null, null, dict);
    }

    /// <summary>
    /// Creates a NavigationDatabase that returns a dummy position (37.0, -122.0) for any fix name.
    /// Useful for parser tests where only the command type matters, not the actual position.
    /// Note: only names registered here will resolve; use <see cref="WithFixes"/> to register specific positions.
    /// </summary>
    internal static NavigationDatabase WithFixNames(params string[] names)
    {
        var dict = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            dict[name] = (37.0, -122.0);
        }

        return NavigationDatabase.ForTesting(dict, null);
    }

    /// <summary>Creates a NavigationDatabase with the given fix positions (params form).</summary>
    internal static NavigationDatabase WithFixes(params (string Name, double Lat, double Lon)[] fixes)
    {
        var dict = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lat, lon) in fixes)
        {
            dict[name] = (lat, lon);
        }

        return NavigationDatabase.ForTesting(dict, null);
    }

    /// <summary>Creates a NavigationDatabase with the given runway(s).</summary>
    internal static NavigationDatabase WithRunways(params RunwayInfo[] runways)
    {
        return NavigationDatabase.ForTesting(null, runways);
    }

    /// <summary>Creates a NavigationDatabase with fixes and a runway.</summary>
    internal static NavigationDatabase WithFixesAndRunways(IReadOnlyList<(string Name, double Lat, double Lon)> fixes, params RunwayInfo[] runways)
    {
        var dict = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lat, lon) in fixes)
        {
            dict[name] = (lat, lon);
        }

        return NavigationDatabase.ForTesting(dict, runways);
    }

    /// <summary>Creates a NavigationDatabase with fix positions and CIFP SID/STAR procedures.</summary>
    internal static NavigationDatabase WithFixesAndProcedures(
        IReadOnlyDictionary<string, (double Lat, double Lon)> fixes,
        IReadOnlyList<CifpSidProcedure>? sids = null,
        IReadOnlyList<CifpStarProcedure>? stars = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? starBodies = null
    )
    {
        return NavigationDatabase.ForTesting(fixes, null, null, null, sids, stars, starBodies);
    }

    /// <summary>
    /// Creates a NavigationDatabase with explicit fix positions, star bodies, star transitions, and airways.
    /// Useful for JARR, JAWY, and navdata-fallback tests.
    /// Pre-seeds the CIFP star cache for the empty-string airport key so that aircraft with
    /// no Destination set don't trigger a CIFP file load (which would fail with an empty path).
    /// </summary>
    internal static NavigationDatabase WithNavData(
        Dictionary<string, (double Lat, double Lon)>? fixPositions = null,
        Dictionary<string, IReadOnlyList<string>>? starBodies = null,
        Dictionary<string, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>>? starTransitions = null,
        Dictionary<string, IReadOnlyList<string>>? airways = null
    )
    {
        // Sentinel star with Airport="" pre-populates _starCache[""] so that JARR dispatch
        // for aircraft with Destination="" skips the CIFP loader (which throws on empty path).
        var sentinelStar = new CifpStarProcedure(
            Airport: "",
            ProcedureId: "__SENTINEL__",
            CommonLegs: [],
            EnrouteTransitions: new Dictionary<string, CifpTransition>(),
            RunwayTransitions: new Dictionary<string, CifpTransition>()
        );
        return NavigationDatabase.ForTesting(fixPositions, null, null, null, null, [sentinelStar], starBodies, starTransitions, airways);
    }

    /// <summary>Creates a CifpApproachProcedure with the given identifiers for use in test NavDbs.</summary>
    internal static CifpApproachProcedure MakeProcedure(
        string airportCode,
        string approachId,
        char typeCode,
        string typeName,
        string? runway,
        IReadOnlyList<CifpLeg>? finalLegs = null,
        IReadOnlyList<CifpLeg>? missedLegs = null
    )
    {
        return new CifpApproachProcedure(
            airportCode,
            approachId,
            typeCode,
            typeName,
            runway,
            finalLegs ?? [],
            new Dictionary<string, CifpTransition>(),
            missedLegs ?? [],
            false,
            null
        );
    }

    /// <summary>Creates a NavigationDatabase with a runway and approach procedures for that airport.</summary>
    internal static NavigationDatabase WithRunwayAndApproaches(RunwayInfo runway, IReadOnlyList<CifpApproachProcedure> approaches)
    {
        var approachesByAirport = new Dictionary<string, IReadOnlyList<CifpApproachProcedure>>(StringComparer.OrdinalIgnoreCase)
        {
            [runway.AirportId] = approaches,
        };

        return NavigationDatabase.ForTesting(null, [runway], approachesByAirport);
    }

    /// <summary>Creates a NavigationDatabase with a runway, approach procedures, and fix positions.</summary>
    internal static NavigationDatabase WithFixesRunwayAndApproaches(
        IReadOnlyList<(string Name, double Lat, double Lon)> fixes,
        RunwayInfo runway,
        IReadOnlyList<CifpApproachProcedure> approaches
    )
    {
        var dict = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lat, lon) in fixes)
        {
            dict[name] = (lat, lon);
        }

        var approachesByAirport = new Dictionary<string, IReadOnlyList<CifpApproachProcedure>>(StringComparer.OrdinalIgnoreCase)
        {
            [runway.AirportId] = approaches,
        };

        return NavigationDatabase.ForTesting(dict, [runway], approachesByAirport);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests;

public class ProgrammedFixesTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static AircraftState MakeAircraft(string route = "", string? destination = "OAK", string? expectedApproach = null)
    {
        return new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Latitude = 37.62,
            Longitude = -122.38,
            Heading = 280,
            Altitude = 10000,
            GroundSpeed = 250,
            Route = route,
            Destination = destination ?? "",
            ExpectedApproach = expectedApproach,
        };
    }

    private static CifpApproachProcedure MakeApproachProcedure()
    {
        return new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
                new CifpLeg("RW28R", CifpPathTerminator.TF, null, null, null, CifpFixRole.MAHP, 40, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            false,
            null
        );
    }

    private static StubApproachLookup MakeApproachLookup()
    {
        return new StubApproachLookup(MakeApproachProcedure());
    }

    // --- GetProgrammedFixes ---

    [Fact]
    public void GetProgrammedFixes_RouteOnly_ReturnsRouteFixes()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");

        var fixes = aircraft.GetProgrammedFixes(null);

        Assert.Contains("SUNOL", fixes);
        Assert.Contains("MODESTO", fixes);
        Assert.Contains("OXNARD", fixes);
        Assert.Equal(3, fixes.Count);
    }

    [Fact]
    public void GetProgrammedFixes_RouteWithAirwaySuffix_StripsCorrectly()
    {
        var aircraft = MakeAircraft(route: "SUNOL.V25 MODESTO");

        var fixes = aircraft.GetProgrammedFixes(null);

        Assert.Contains("SUNOL", fixes);
        Assert.Contains("MODESTO", fixes);
        Assert.DoesNotContain("V25", fixes);
        Assert.Equal(2, fixes.Count);
    }

    [Fact]
    public void GetProgrammedFixes_ExpectedApproachOnly_ReturnsApproachFixes()
    {
        var aircraft = MakeAircraft(expectedApproach: "I28R");
        var approachLookup = MakeApproachLookup();

        var fixes = aircraft.GetProgrammedFixes(approachLookup);

        Assert.Contains("GROVE", fixes);
        Assert.Contains("FITKI", fixes);
        Assert.Contains("BERYL", fixes);
        // MAHP (RW28R) should NOT be included — stops before MAHP
        Assert.DoesNotContain("RW28R", fixes);
    }

    [Fact]
    public void GetProgrammedFixes_ActiveApproachOnly_ReturnsApproachFixes()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeApproachProcedure();

        // Simulate active approach via PhaseList
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = 280,
                Procedure = procedure,
            },
        };

        var fixes = aircraft.GetProgrammedFixes(null);

        Assert.Contains("GROVE", fixes);
        Assert.Contains("FITKI", fixes);
        Assert.Contains("BERYL", fixes);
        Assert.DoesNotContain("RW28R", fixes);
    }

    [Fact]
    public void GetProgrammedFixes_Combined_ReturnsUnion()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO", expectedApproach: "I28R");
        var approachLookup = MakeApproachLookup();

        var fixes = aircraft.GetProgrammedFixes(approachLookup);

        Assert.Contains("SUNOL", fixes);
        Assert.Contains("MODESTO", fixes);
        Assert.Contains("GROVE", fixes);
        Assert.Contains("FITKI", fixes);
        Assert.Contains("BERYL", fixes);
        Assert.Equal(5, fixes.Count);
    }

    [Fact]
    public void GetProgrammedFixes_EmptyState_ReturnsEmpty()
    {
        var aircraft = MakeAircraft(route: "", destination: null);

        var fixes = aircraft.GetProgrammedFixes(null);

        Assert.Empty(fixes);
    }

    [Fact]
    public void GetProgrammedFixes_CaseInsensitive()
    {
        var aircraft = MakeAircraft(route: "sunol MODESTO");

        var fixes = aircraft.GetProgrammedFixes(null);

        Assert.Contains("sunol", fixes);
        Assert.Contains("SUNOL", fixes);
    }

    // --- DCT validation ---

    [Fact]
    public void Dct_ToProgrammedFix_Accepted()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8));
        var cmd = new DirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, validateDctFixes: true);

        Assert.True(result.Success);
    }

    [Fact]
    public void Dct_ToNonProgrammedFix_Rejected()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        var fixes = new StubFixLookup(("RANDOM", 37.0, -121.0));
        var cmd = new DirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, validateDctFixes: true);

        Assert.False(result.Success);
        Assert.Contains("not programmed", result.Message);
        Assert.Contains("DCTF", result.Message);
    }

    [Fact]
    public void Dct_ToExpectedApproachFix_Accepted()
    {
        var aircraft = MakeAircraft(expectedApproach: "I28R");
        var approachLookup = MakeApproachLookup();
        var fixes = new StubFixLookup(("GROVE", 37.78, -122.35));
        var cmd = new DirectToCommand([new ResolvedFix("GROVE", 37.78, -122.35)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, approachLookup, validateDctFixes: true);

        Assert.True(result.Success);
    }

    [Fact]
    public void Dctf_ToNonProgrammedFix_Accepted()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        var fixes = new StubFixLookup(("RANDOM", 37.0, -121.0));
        var cmd = new ForceDirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, validateDctFixes: true);

        Assert.True(result.Success);
    }

    [Fact]
    public void Adct_ToNonProgrammedFix_Rejected()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        var fixes = new StubFixLookup(("RANDOM", 37.0, -121.0));
        var cmd = new AppendDirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, validateDctFixes: true);

        Assert.False(result.Success);
        Assert.Contains("not programmed", result.Message);
    }

    [Fact]
    public void Dct_EmptyProgrammedSet_AllowsAnyFix()
    {
        // Aircraft with no route, no expected approach = empty programmed set → backward compat
        var aircraft = MakeAircraft(route: "");
        var fixes = new StubFixLookup(("RANDOM", 37.0, -121.0));
        var cmd = new DirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, validateDctFixes: true);

        Assert.True(result.Success);
    }

    [Fact]
    public void Dct_ValidationDisabled_AllowsNonProgrammedFix()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        var fixes = new StubFixLookup(("RANDOM", 37.0, -121.0));
        var cmd = new DirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, validateDctFixes: false);

        Assert.True(result.Success);
    }

    [Fact]
    public void Dct_MultipleFixes_RejectsIfAnyNonProgrammed()
    {
        var aircraft = MakeAircraft(route: "SUNOL MODESTO");
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8), ("RANDOM", 37.0, -121.0));
        var cmd = new DirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8), new ResolvedFix("RANDOM", 37.0, -121.0)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Logger, Random.Shared, validateDctFixes: true);

        Assert.False(result.Success);
        Assert.Contains("RANDOM", result.Message);
        Assert.DoesNotContain("SUNOL", result.Message!);
    }

    // --- Test helpers ---

    private sealed class StubFixLookup : IFixLookup
    {
        private readonly Dictionary<string, (double Lat, double Lon)> _fixes = new(StringComparer.OrdinalIgnoreCase);

        public StubFixLookup(params (string Name, double Lat, double Lon)[] fixes)
        {
            foreach (var (name, lat, lon) in fixes)
            {
                _fixes[name] = (lat, lon);
            }
        }

        public (double Lat, double Lon)? GetFixPosition(string name) => _fixes.TryGetValue(name, out var pos) ? pos : null;

        public double? GetAirportElevation(string code) => null;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }

    private sealed class StubApproachLookup : IApproachLookup
    {
        private readonly CifpApproachProcedure _procedure;

        public StubApproachLookup(CifpApproachProcedure procedure)
        {
            _procedure = procedure;
        }

        public CifpApproachProcedure? GetApproach(string airportCode, string approachId)
        {
            string normalized = NormalizeAirport(airportCode);
            return
                normalized.Equals(_procedure.Airport, StringComparison.OrdinalIgnoreCase)
                && approachId.Equals(_procedure.ApproachId, StringComparison.OrdinalIgnoreCase)
                ? _procedure
                : null;
        }

        public IReadOnlyList<CifpApproachProcedure> GetApproaches(string airportCode)
        {
            string normalized = NormalizeAirport(airportCode);
            return normalized.Equals(_procedure.Airport, StringComparison.OrdinalIgnoreCase) ? [_procedure] : [];
        }

        public string? ResolveApproachId(string airportCode, string shorthand)
        {
            string normalized = NormalizeAirport(airportCode);
            if (!normalized.Equals(_procedure.Airport, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (shorthand.Equals(_procedure.ApproachId, StringComparison.OrdinalIgnoreCase))
            {
                return _procedure.ApproachId;
            }

            string fullName = _procedure.ApproachTypeName + _procedure.Runway;
            return fullName.Equals(shorthand, StringComparison.OrdinalIgnoreCase) ? _procedure.ApproachId : null;
        }

        private static string NormalizeAirport(string code)
        {
            string upper = code.ToUpperInvariant();
            return upper.StartsWith('K') && upper.Length == 4 ? upper[1..] : upper;
        }
    }
}

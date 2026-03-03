using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class ExpectApproachTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static AircraftState MakeAircraft(string destination = "OAK")
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = 280,
            Altitude = 5000,
            GroundSpeed = 250,
            Latitude = 37.75,
            Longitude = -122.35,
            Destination = destination,
        };
    }

    private static RunwayInfo MakeRunway()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );
    }

    private static (StubApproachLookup, StubRunwayLookup) MakeStubs()
    {
        var procedure = new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            false,
            null
        );

        return (new StubApproachLookup(procedure), new StubRunwayLookup(MakeRunway()));
    }

    [Fact]
    public void Eapp_SetsExpectedApproach()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup) = MakeStubs();

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Logger, approachLookup);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }

    [Fact]
    public void Eapp_ReturnsConfirmationMessage()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup) = MakeStubs();

        var cmd = new ExpectApproachCommand("I28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Logger, approachLookup);

        Assert.True(result.Success);
        Assert.Contains("Expecting", result.Message);
        Assert.Contains("I28R", result.Message);
    }

    [Fact]
    public void Eapp_WithExplicitAirport_SetsExpectedApproach()
    {
        var aircraft = MakeAircraft(destination: "SFO");
        var (approachLookup, runwayLookup) = MakeStubs();

        // Explicit airport overrides destination
        var cmd = new ExpectApproachCommand("ILS28R", "OAK");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Logger, approachLookup);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }

    [Fact]
    public void Eapp_UnknownApproach_ReturnsError()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup) = MakeStubs();

        var cmd = new ExpectApproachCommand("VOR99", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Logger, approachLookup);

        Assert.False(result.Success);
        Assert.Contains("Unknown approach", result.Message);
    }

    [Fact]
    public void Eapp_NoApproachLookup_ReturnsError()
    {
        var aircraft = MakeAircraft();
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Logger);

        Assert.False(result.Success);
        Assert.Contains("not available", result.Message);
    }

    [Fact]
    public void Eapp_OverwritesPreviousExpectedApproach()
    {
        var aircraft = MakeAircraft();
        aircraft.ExpectedApproach = "V28L";
        var (approachLookup, runwayLookup) = MakeStubs();

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Logger, approachLookup);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }

    [Fact]
    public void Eapp_ResolvesShorthandId()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup) = MakeStubs();

        // "ILS28R" should resolve to "I28R"
        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Logger, approachLookup);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }

    // --- Test helpers ---

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

    private sealed class StubRunwayLookup : IRunwayLookup
    {
        private readonly RunwayInfo? _runway;

        public StubRunwayLookup(RunwayInfo? runway = null)
        {
            _runway = runway;
        }

        public RunwayInfo? GetRunway(string airportCode, string runwayId)
        {
            if (_runway is null)
            {
                return null;
            }

            string normalizedCode = airportCode.StartsWith('K') && airportCode.Length == 4 ? airportCode[1..] : airportCode;
            string normalizedRunway = _runway.AirportId.StartsWith('K') && _runway.AirportId.Length == 4 ? _runway.AirportId[1..] : _runway.AirportId;

            return
                normalizedCode.Equals(normalizedRunway, StringComparison.OrdinalIgnoreCase)
                && _runway.Designator.Equals(runwayId, StringComparison.OrdinalIgnoreCase)
                ? _runway
                : null;
        }

        public IReadOnlyList<RunwayInfo> GetRunways(string airportCode)
        {
            return _runway is not null ? [_runway] : [];
        }
    }
}

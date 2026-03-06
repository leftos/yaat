using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class DepartureClearanceHandlerTests
{
    private static readonly ILogger Logger = new NullLogger<DepartureClearanceHandlerTests>();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(string departure = "OAK", string? route = null)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = 37.728,
            Longitude = -122.218,
            Heading = 280,
            Altitude = 6,
            GroundSpeed = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Departure = departure,
            Route = route!,
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);

    private static HoldingShortPhase MakeHoldingShort(string runway = "30/12")
    {
        return new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 10,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = runway,
            }
        );
    }

    private static PhaseContext MinCtx(AircraftState ac) => CommandDispatcher.BuildMinimalContext(ac, NullLogger.Instance);

    // -------------------------------------------------------------------------
    // TryDepartureClearance from HoldingShort
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_FromHoldingShort_LUAW_Succeeds()
    {
        var ac = MakeAircraft();
        var holding = MakeHoldingShort();
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        var rwy = DefaultRunway();
        var runways = new StubRunwayLookup(rwy);

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.LineUpAndWait,
            new DefaultDeparture(),
            null,
            runways,
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.Contains("Line up and wait", result.Message!);
        Assert.NotNull(ac.Phases.AssignedRunway);
    }

    [Fact]
    public void TryDepartureClearance_FromHoldingShort_CTO_Succeeds()
    {
        var ac = MakeAircraft();
        var holding = MakeHoldingShort();
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        var rwy = DefaultRunway();
        var runways = new StubRunwayLookup(rwy);

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new RunwayHeadingDeparture(),
            null,
            runways,
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.Contains("Cleared for takeoff", result.Message!);
        Assert.Contains("runway heading", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryDepartureClearance from Taxiing (pre-store)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_FromTaxiing_StoresClearance()
    {
        var ac = MakeAircraft();
        var taxiPhase = new TaxiingPhase();
        ac.Phases!.Add(taxiPhase);
        ac.Phases.Start(MinCtx(ac));

        // Need a taxi route with a destination hold-short
        ac.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "30/12",
                },
            ],
        };

        var rwy = DefaultRunway();
        var runways = new StubRunwayLookup(rwy);

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            taxiPhase,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            5000,
            runways,
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases.DepartureClearance);
        Assert.Equal(ClearanceType.ClearedForTakeoff, ac.Phases.DepartureClearance!.Type);
        Assert.Equal(5000, ac.Phases.DepartureClearance.AssignedAltitude);
    }

    [Fact]
    public void TryDepartureClearance_FromTaxiing_NoRoute_Fails()
    {
        var ac = MakeAircraft();
        var taxiPhase = new TaxiingPhase();
        ac.Phases!.Add(taxiPhase);
        ac.Phases.Start(MinCtx(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            taxiPhase,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            null,
            null,
            null,
            Logger
        );

        Assert.False(result.Success);
        Assert.Contains("No taxi route", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryDepartureClearance from LineUp (pre-satisfy)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_FromLineUp_CTO_SatisfiesUpcomingLUAW()
    {
        var ac = MakeAircraft();
        var rwy = DefaultRunway();
        ac.Phases!.AssignedRunway = rwy;

        var lineUp = new LineUpPhase(null);
        var luaw = new LinedUpAndWaitingPhase();
        var takeoff = new TakeoffPhase();
        var climb = new InitialClimbPhase { Departure = new DefaultDeparture() };

        ac.Phases.Add(lineUp);
        ac.Phases.Add(luaw);
        ac.Phases.Add(takeoff);
        ac.Phases.Add(climb);
        ac.Phases.Start(MinCtx(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            lineUp,
            ClearanceType.ClearedForTakeoff,
            new RunwayHeadingDeparture(),
            null,
            null,
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.True(luaw.Requirements[0].IsSatisfied);
    }

    [Fact]
    public void TryDepartureClearance_FromLineUp_LUAW_Rejected()
    {
        var ac = MakeAircraft();
        var lineUp = new LineUpPhase(null);
        ac.Phases!.Add(lineUp);
        ac.Phases.Start(MinCtx(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            lineUp,
            ClearanceType.LineUpAndWait,
            new DefaultDeparture(),
            null,
            null,
            null,
            Logger
        );

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // Closed-traffic departure
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_ClosedTraffic_SetsPatternMode_NoInitialClimb()
    {
        var ac = MakeAircraft();
        var holding = MakeHoldingShort();
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        var rwy = DefaultRunway();
        var runways = new StubRunwayLookup(rwy);

        var departure = new ClosedTrafficDeparture(PatternDirection.Left);
        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            departure,
            null,
            runways,
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.Equal(PatternDirection.Left, ac.Phases.TrafficDirection);

        // No InitialClimbPhase should be in the phase list
        Assert.DoesNotContain(ac.Phases.Phases, p => p is InitialClimbPhase);
    }

    // -------------------------------------------------------------------------
    // ResolveLegsToTargets
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveLegsToTargets_PI_Skipped()
    {
        var fixes = new StubFixLookup(new Dictionary<string, (double, double)> { ["FIXPI"] = (37.0, -122.0), ["FIXNORM"] = (37.5, -122.5) });

        var legs = new[]
        {
            new CifpLeg("FIXPI", CifpPathTerminator.PI, null, null, null, CifpFixRole.None, 1, null, null, null),
            new CifpLeg("FIXNORM", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 2, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        // PI leg should be skipped, only FIXNORM should appear
        Assert.Single(targets);
        Assert.Equal("FIXNORM", targets[0].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_UnknownFix_Skipped()
    {
        var fixes = new StubFixLookup(new Dictionary<string, (double, double)> { ["KNOWN"] = (37.0, -122.0) });

        var legs = new[]
        {
            new CifpLeg("UNKNOWN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 1, null, null, null),
            new CifpLeg("KNOWN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 2, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.Single(targets);
        Assert.Equal("KNOWN", targets[0].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_WithAltitudeConstraint_Preserved()
    {
        var fixes = new StubFixLookup(new Dictionary<string, (double, double)> { ["FIX1"] = (37.0, -122.0) });

        var alt = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 5000);
        var legs = new[] { new CifpLeg("FIX1", CifpPathTerminator.TF, null, alt, null, CifpFixRole.None, 1, null, null, null) };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.Single(targets);
        Assert.NotNull(targets[0].AltitudeRestriction);
        Assert.Equal(5000, targets[0].AltitudeRestriction!.Altitude1Ft);
    }

    [Fact]
    public void ResolveLegsToTargets_DuplicateAdjacentFix_Deduplicated()
    {
        var fixes = new StubFixLookup(new Dictionary<string, (double, double)> { ["FIX1"] = (37.0, -122.0) });

        var legs = new[]
        {
            new CifpLeg("FIX1", CifpPathTerminator.IF, null, null, null, CifpFixRole.None, 1, null, null, null),
            new CifpLeg("FIX1", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 2, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.Single(targets);
    }

    // -------------------------------------------------------------------------
    // BuildDepartureMessage
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildDepartureMessage_CTO_WithAltitude()
    {
        var result = DepartureClearanceHandler.BuildDepartureMessage(ClearanceType.ClearedForTakeoff, "30", new DefaultDeparture(), 5000);

        Assert.True(result.Success);
        Assert.Contains("Cleared for takeoff runway 30", result.Message!);
        Assert.Contains("climb and maintain 5,000", result.Message!);
    }

    [Fact]
    public void BuildDepartureMessage_LUAW()
    {
        var result = DepartureClearanceHandler.BuildDepartureMessage(ClearanceType.LineUpAndWait, "30", new DefaultDeparture(), null);

        Assert.True(result.Success);
        Assert.Contains("Line up and wait", result.Message!);
    }

    // -------------------------------------------------------------------------
    // FormatDepartureInstructionSuffix
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(DefaultDeparture), "")]
    [InlineData(typeof(RunwayHeadingDeparture), ", fly runway heading")]
    [InlineData(typeof(OnCourseDeparture), ", on course")]
    public void FormatDepartureInstructionSuffix_MatchesExpected(Type depType, string expected)
    {
        var dep = (DepartureInstruction)Activator.CreateInstance(depType)!;
        Assert.Equal(expected, DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep));
    }

    [Fact]
    public void FormatDepartureInstructionSuffix_DirectFix()
    {
        var dep = new DirectFixDeparture("EDDYY", 37.0, -122.0);
        Assert.Equal(", direct EDDYY", DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep));
    }

    [Fact]
    public void FormatDepartureInstructionSuffix_ClosedTraffic()
    {
        var dep = new ClosedTrafficDeparture(PatternDirection.Right);
        Assert.Contains("right traffic", DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep));
    }

    // -------------------------------------------------------------------------
    // Stub helpers
    // -------------------------------------------------------------------------

    private sealed class StubRunwayLookup : IRunwayLookup
    {
        private readonly RunwayInfo _runway;

        public StubRunwayLookup(RunwayInfo runway) => _runway = runway;

        public RunwayInfo? GetRunway(string airportCode, string designator)
        {
            if (_runway.Id.Contains(designator))
            {
                return _runway.ForApproach(designator);
            }
            return null;
        }

        public IReadOnlyList<RunwayInfo> GetRunways(string airportCode) => [_runway];
    }

    private sealed class StubFixLookup : IFixLookup
    {
        private readonly Dictionary<string, (double Lat, double Lon)> _fixes;

        public StubFixLookup(Dictionary<string, (double, double)> fixes) => _fixes = fixes;

        public (double Lat, double Lon)? GetFixPosition(string name) => _fixes.TryGetValue(name, out var pos) ? pos : null;

        public double? GetAirportElevation(string code) => null;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }
}

using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class ApproachCommandHandlerTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    private static AircraftState MakeAircraft(
        double heading = 280,
        double altitude = 3000,
        double lat = 37.75,
        double lon = -122.35,
        string destination = "OAK",
        double? speed = null
    )
    {
        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = heading,
            Altitude = altitude,
            GroundSpeed = 180,
            Latitude = lat,
            Longitude = lon,
            Destination = destination,
        };

        if (speed is not null)
        {
            ac.Targets.TargetSpeed = speed;
        }

        return ac;
    }

    private static RunwayInfo MakeRunway(string designator = "28R", string airportId = "OAK", double heading = 280)
    {
        return TestRunwayFactory.Make(
            designator: designator,
            airportId: airportId,
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: heading,
            elevationFt: 9
        );
    }

    // --- CAPP basic ---

    [Fact]
    public void Capp_Basic_CreatesPhaseSequence()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains("I28R", result.Message);
    }

    [Fact]
    public void Capp_SetsActiveApproach()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
        Assert.Equal("OAK", aircraft.Phases.ActiveApproach.AirportCode);
        Assert.Equal("28R", aircraft.Phases.ActiveApproach.RunwayId);
    }

    [Fact]
    public void Capp_CancelsSpeedRestriction()
    {
        var aircraft = MakeAircraft(speed: 210);
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        Assert.Equal(210, aircraft.Targets.TargetSpeed);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void Capp_WithAtFix_PrependsFixToApproachNav()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("ILS28R", null, false, "SUNOL", 37.5, -121.8, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        var navPhase = aircraft.Phases!.Phases.OfType<ApproachNavigationPhase>().Single();
        Assert.Equal("SUNOL", navPhase.Fixes[0].Name);
    }

    [Fact]
    public void Capp_WithDctFix_PrependsFixToApproachNav()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, "SUNOL", 37.5, -121.8, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        var navPhase = aircraft.Phases!.Phases.OfType<ApproachNavigationPhase>().Single();
        Assert.Equal("SUNOL", navPhase.Fixes[0].Name);
    }

    [Fact]
    public void Capp_WithCrossFixAltitude_SetsAltitude()
    {
        var aircraft = MakeAircraft(altitude: 5000);
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, "SUNOL", 37.5, -121.8, 3400, CrossFixAltitudeType.At);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        Assert.Equal(3400, aircraft.Targets.TargetAltitude);
    }

    // --- Intercept angle validation ---

    [Fact]
    public void Capp_RejectsLargeInterceptAngle()
    {
        // Aircraft heading 180 vs final course 280 = 100° intercept
        var aircraft = MakeAircraft(heading: 180);
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.False(result.Success);
        Assert.Contains("Intercept angle", result.Message);
        Assert.Contains("5-9-2", result.Message);
    }

    [Fact]
    public void CappForce_BypassesInterceptCheck()
    {
        var aircraft = MakeAircraft(heading: 180);
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("ILS28R", null, true, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
    }

    // --- JAPP ---

    [Fact]
    public void Japp_CreatesPhaseSequence()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new JoinApproachCommand("ILS28R", null, false);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains("Join", result.Message);
    }

    [Fact]
    public void Japp_CancelsSpeedRestriction()
    {
        var aircraft = MakeAircraft(speed: 210);
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new JoinApproachCommand("ILS28R", null, false);
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void JappForce_BypassesInterceptCheck()
    {
        var aircraft = MakeAircraft(heading: 180);
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new JoinApproachCommand("ILS28R", null, true);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
    }

    // --- CAPPSI / JAPPSI (straight-in) ---

    [Fact]
    public void Cappsi_SkipsHoldInLieu()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubsWithHoldInLieu();

        var cmd = new ClearedApproachStraightInCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        Assert.Contains("straight-in", result.Message);

        // No holding pattern phase
        Assert.NotNull(aircraft.Phases);
        Assert.DoesNotContain(aircraft.Phases.Phases, p => p is HoldingPatternPhase);
    }

    [Fact]
    public void Jappsi_SkipsHoldInLieu()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubsWithHoldInLieu();

        var cmd = new JoinApproachStraightInCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is HoldingPatternPhase);
    }

    // --- Hold-in-lieu ---

    [Fact]
    public void Japp_WithHoldInLieu_InsertsHoldPhase()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubsWithHoldInLieu();

        var cmd = new JoinApproachCommand("ILS28R", null, false);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains(aircraft.Phases.Phases, p => p is HoldingPatternPhase);

        var holdPhase = aircraft.Phases.Phases.OfType<HoldingPatternPhase>().First();
        Assert.Equal(1, holdPhase.MaxCircuits);
    }

    // --- PTAC ---

    [Fact]
    public void Ptac_SetsHeadingAndAltitude()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, _) = MakeStubs();

        var cmd = new PositionTurnAltitudeClearanceCommand(340, 2500, "ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup);

        Assert.True(result.Success);
        Assert.Equal(340, aircraft.Targets.TargetHeading);
        Assert.Equal(2500, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void Ptac_CreatesInterceptPhaseSequence()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, _) = MakeStubs();

        var cmd = new PositionTurnAltitudeClearanceCommand(340, 2500, "ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup);

        Assert.NotNull(aircraft.Phases);
        Assert.Equal(3, aircraft.Phases.Phases.Count);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases.Phases[0]);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.Phases[1]);
        Assert.IsType<LandingPhase>(aircraft.Phases.Phases[2]);
    }

    [Fact]
    public void Ptac_CancelsSpeedRestriction()
    {
        var aircraft = MakeAircraft(speed: 210);
        var (approachLookup, runwayLookup, _) = MakeStubs();

        var cmd = new PositionTurnAltitudeClearanceCommand(340, 2500, "ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup);

        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void Ptac_SetsActiveApproach()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, _) = MakeStubs();

        var cmd = new PositionTurnAltitudeClearanceCommand(340, 2500, "ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup);

        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
    }

    // --- ApproachNavigationPhase ---

    [Fact]
    public void ApproachNavPhase_NavigatesThroughFixes()
    {
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30), new("FIX2", 37.75, -122.25) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(lat: 37.80, lon: -122.30);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        // Aircraft is at FIX1 — should advance to FIX2
        bool done = phase.OnTick(ctx);
        Assert.False(done);
        Assert.Contains(aircraft.Targets.NavigationRoute, t => t.Name == "FIX2");
    }

    [Fact]
    public void ApproachNavPhase_CompletesAtLastFix()
    {
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(lat: 37.80, lon: -122.30);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        // Aircraft is at the only fix — should complete
        bool done = phase.OnTick(ctx);
        Assert.True(done);
    }

    [Fact]
    public void ApproachNavPhase_AppliesAltitudeRestriction()
    {
        var altRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 3000);
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30, altRestriction), new("FIX2", 37.75, -122.25) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(altitude: 5000, lat: 37.85, lon: -122.35);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        Assert.Equal(3000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ApproachNavPhase_ClearsPhaseOnHeadingCommand()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    [Fact]
    public void ApproachNavPhase_AllowsLandingClearance()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
    }

    [Fact]
    public void ApproachNavPhase_AllowsSpeedCommand()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Speed));
    }

    [Fact]
    public void ApproachNavPhase_AllowsAltitudeCommand()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.DescendMaintain));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
    }

    [Fact]
    public void InterceptPhase_AllowsSpeedCommand()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = 280,
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Speed));
    }

    [Fact]
    public void InterceptPhase_ClearsPhaseOnHeadingCommand()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = 280,
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    [Fact]
    public void ApproachNavPhase_AppliesGlideSlopeInterceptAltitude()
    {
        var altRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.GlideSlopeIntercept, 1800);
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30, altRestriction), new("FIX2", 37.75, -122.25) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(altitude: 3000, lat: 37.85, lon: -122.35);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        Assert.Equal(1800, aircraft.Targets.TargetAltitude);
    }

    // --- Error cases ---

    [Fact]
    public void Capp_UnknownApproach_Fails()
    {
        var aircraft = MakeAircraft();
        var (approachLookup, runwayLookup, fixLookup) = MakeStubs();

        var cmd = new ClearedApproachCommand("VOR99", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, fixLookup, Random.Shared, approachLookup);

        Assert.False(result.Success);
        Assert.Contains("Unknown approach", result.Message);
    }

    [Fact]
    public void Capp_NoApproachLookup_Fails()
    {
        var aircraft = MakeAircraft();
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared);

        Assert.False(result.Success);
        Assert.Contains("not available", result.Message);
    }

    // --- Helpers ---

    private static PhaseContext MakeContext(AircraftState aircraft)
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
        };
    }

    private static (StubApproachLookup, StubRunwayLookup, StubFixLookup) MakeStubs()
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

        var approachLookup = new StubApproachLookup(procedure);
        var runwayLookup = new StubRunwayLookup(MakeRunway());
        var fixLookup = new StubFixLookup(("GROVE", 37.78, -122.35), ("FITKI", 37.76, -122.30), ("BERYL", 37.74, -122.26));

        return (approachLookup, runwayLookup, fixLookup);
    }

    private static (StubApproachLookup, StubRunwayLookup, StubFixLookup) MakeStubsWithHoldInLieu()
    {
        var holdLeg = new CifpLeg("FITKI", CifpPathTerminator.HF, 'R', null, null, CifpFixRole.IF, 20, null, null, null);

        var procedure = new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                holdLeg,
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            true,
            holdLeg
        );

        var approachLookup = new StubApproachLookup(procedure);
        var runwayLookup = new StubRunwayLookup(MakeRunway());
        var fixLookup = new StubFixLookup(("GROVE", 37.78, -122.35), ("FITKI", 37.76, -122.30), ("BERYL", 37.74, -122.26));

        return (approachLookup, runwayLookup, fixLookup);
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
}

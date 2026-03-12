using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class ApproachClearanceTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    private static AircraftState MakeAircraft(
        double heading = 090,
        double altitude = 3000,
        double lat = 37.75,
        double lon = -122.35,
        string destination = "OAK"
    )
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = heading,
            Altitude = altitude,
            Latitude = lat,
            Longitude = lon,
            Destination = destination,
        };
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

    // --- ApproachClearance record ---

    [Fact]
    public void ApproachClearance_StoredOnPhaseList()
    {
        var clearance = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        var phases = new PhaseList { ActiveApproach = clearance };

        Assert.NotNull(phases.ActiveApproach);
        Assert.Equal("I28R", phases.ActiveApproach.ApproachId);
        Assert.Equal("28R", phases.ActiveApproach.RunwayId);
        Assert.Equal(280, phases.ActiveApproach.FinalApproachCourse);
    }

    [Fact]
    public void ApproachClearance_DefaultsToNotStraightInNotForced()
    {
        var clearance = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = 280,
        };

        Assert.False(clearance.StraightIn);
        Assert.False(clearance.Force);
        Assert.Null(clearance.Procedure);
    }

    // --- InterceptCoursePhase ---

    [Fact]
    public void InterceptCoursePhase_CompletesWhenAlignedAndOnCourse()
    {
        // Use heading 360 (due north) for simplicity — the extended centerline
        // is along the same longitude as the threshold.
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = 360,
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };

        // Aircraft directly south of threshold on centerline, heading north
        var aircraft = MakeAircraft(heading: 360, lat: 37.65, lon: -122.22);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        bool done = phase.OnTick(ctx);

        Assert.True(done);
        Assert.Equal(360, aircraft.Targets.TargetHeading);
    }

    [Fact]
    public void InterceptCoursePhase_NotCompleteWhenFarFromCourse()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = 360,
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };

        // Aircraft is 0.1° east of the course line (~5nm cross-track)
        var aircraft = MakeAircraft(heading: 360, lat: 37.65, lon: -122.12);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        bool done = phase.OnTick(ctx);

        Assert.False(done);
    }

    [Fact]
    public void InterceptCoursePhase_NotCompleteWhenHeadingNotAligned()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = 360,
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };

        // Aircraft is on the course line but heading 45° off
        var aircraft = MakeAircraft(heading: 315, lat: 37.65, lon: -122.22);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);
        bool done = phase.OnTick(ctx);

        Assert.False(done);
    }

    [Fact]
    public void InterceptCoursePhase_ClearsPhaseOnNonApproachCommand()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = 280,
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };

        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.DirectTo));
    }

    [Fact]
    public void InterceptCoursePhase_AllowsApproachCommands()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = 280,
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ExitLeft));
    }

    // --- JFAC dispatch ---

    [Fact]
    public void Jfac_CreatesPhaseSequence()
    {
        var aircraft = MakeAircraft(heading: 300, destination: "OAK");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Equal(3, aircraft.Phases.Phases.Count);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases.Phases[0]);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.Phases[1]);
        Assert.IsType<LandingPhase>(aircraft.Phases.Phases[2]);
    }

    [Fact]
    public void Jfac_SetsActiveApproach()
    {
        var aircraft = MakeAircraft(heading: 300, destination: "OAK");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
        Assert.Equal("OAK", aircraft.Phases.ActiveApproach.AirportCode);
        Assert.Equal("28R", aircraft.Phases.ActiveApproach.RunwayId);
        Assert.Equal(280, aircraft.Phases.ActiveApproach.FinalApproachCourse);
    }

    [Fact]
    public void Jfac_SetsAssignedRunway()
    {
        var aircraft = MakeAircraft(heading: 300, destination: "OAK");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.NotNull(aircraft.Phases?.AssignedRunway);
        Assert.Equal("28R", aircraft.Phases.AssignedRunway.Designator);
    }

    [Fact]
    public void Jfac_StartsInterceptPhase()
    {
        var aircraft = MakeAircraft(heading: 300, destination: "OAK");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, aircraft.Phases.CurrentPhase.Status);
    }

    [Fact]
    public void Jfac_UnknownApproach_Fails()
    {
        var aircraft = MakeAircraft(destination: "OAK");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("VOR99");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown approach", result.Message);
    }

    [Fact]
    public void Jfac_NoApproachLookup_Fails()
    {
        var aircraft = MakeAircraft(destination: "OAK");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, null, null, true);

        Assert.False(result.Success);
        Assert.Contains("not available", result.Message);
    }

    [Fact]
    public void Jfac_NoDestination_Fails()
    {
        var aircraft = MakeAircraft(destination: "");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.False(result.Success);
        Assert.Contains("Cannot determine airport", result.Message);
    }

    [Fact]
    public void Jfac_ClearsExistingPhases()
    {
        var aircraft = MakeAircraft(heading: 300, destination: "OAK");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        // Set up existing phases
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(
            new HoldingPatternPhase
            {
                FixName = "OAK",
                FixLat = 37.72,
                FixLon = -122.22,
                InboundCourse = 280,
                LegLength = 1,
                IsMinuteBased = true,
                Direction = TurnDirection.Right,
            }
        );

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.True(result.Success);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases!.CurrentPhase);
    }

    [Fact]
    public void Jfac_ResolvesShorthand()
    {
        var aircraft = MakeAircraft(heading: 300, destination: "OAK");
        // ApproachId is "I28R" but user types "ILS28R"
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.True(result.Success);
        Assert.Contains("I28R", result.Message);
    }

    [Fact]
    public void Jfac_IcaoDestination_Normalized()
    {
        var aircraft = MakeAircraft(heading: 300, destination: "KOAK");
        var approachLookup = new StubApproachLookup("OAK", "I28R", 'I', "ILS", "28R");
        var runwayLookup = new StubRunwayLookup(MakeRunway());

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, runwayLookup, null, null, Random.Shared, approachLookup, null, true);

        Assert.True(result.Success);
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

    private sealed class StubApproachLookup : IApproachLookup
    {
        private readonly Dictionary<string, List<CifpApproachProcedure>> _approaches = new(StringComparer.OrdinalIgnoreCase);

        public StubApproachLookup(string airport, string approachId, char typeCode, string typeName, string runway)
        {
            var procedure = new CifpApproachProcedure(
                airport,
                approachId,
                typeCode,
                typeName,
                runway,
                [],
                new Dictionary<string, CifpTransition>(),
                [],
                false,
                null
            );

            _approaches[airport] = [procedure];
        }

        public CifpApproachProcedure? GetApproach(string airportCode, string approachId)
        {
            string normalized = NormalizeAirport(airportCode);
            if (!_approaches.TryGetValue(normalized, out var list))
            {
                return null;
            }

            return list.FirstOrDefault(a => a.ApproachId.Equals(approachId, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<CifpApproachProcedure> GetApproaches(string airportCode)
        {
            string normalized = NormalizeAirport(airportCode);
            return _approaches.TryGetValue(normalized, out var list) ? list : [];
        }

        public string? ResolveApproachId(string airportCode, string shorthand)
        {
            string normalized = NormalizeAirport(airportCode);
            if (!_approaches.TryGetValue(normalized, out var list))
            {
                return null;
            }

            // Exact match first
            var exact = list.FirstOrDefault(a => a.ApproachId.Equals(shorthand, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact.ApproachId;
            }

            // Try type prefix matching (simplified)
            foreach (var proc in list)
            {
                string fullName = proc.ApproachTypeName + proc.Runway;
                if (fullName.Equals(shorthand, StringComparison.OrdinalIgnoreCase))
                {
                    return proc.ApproachId;
                }
            }

            return null;
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

using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A hold takes the aircraft off its vector, so the controller-assigned heading is cleared when the hold starts
/// (<c>ControlTargets.AssignedMagneticHeading</c> is null whenever the aircraft is not on a vector). Each test
/// sets the vector with <c>FH 090</c> first, then issues the hold.
/// </summary>
public class HoldClearsAssignedHeadingTests(ITestOutputHelper output)
{
    private const string FixName = "SUNOL";
    private const int InboundCourse = 184;
    private const double AssignedDegrees = 90;
    private const int MaxTicks = 240;

    private SimulationEngine? BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            output.WriteLine("Skipped: OAK layout not available");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "hold-clears-assigned-heading",
                ScenarioName = "hold clears assigned heading",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.GroundLayout = layout;
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        return engine;
    }

    private static LatLon FixPosition()
    {
        (double Lat, double Lon)? position = NavigationDatabase.Instance.GetFixPosition(FixName);
        Assert.NotNull(position);
        return new LatLon(position.Value.Lat, position.Value.Lon);
    }

    private static AircraftState AddAirborne(SimulationEngine engine, string callsign, LatLon position, string flightRules)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(180),
            Altitude = 5000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "OAK",
                FlightRules = flightRules,
            },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    private static void IssueVector(SimulationEngine engine, AircraftState ac)
    {
        CommandResult result = engine.SendCommand(ac.Callsign, "FH 090");
        Assert.True(result.Success, result.Message);
        Assert.Equal(AssignedDegrees, ac.Targets.AssignedMagneticHeading?.Degrees);
    }

    private static string HoldCommand() => $"HOLDP {FixName} {InboundCourse} 1M R";

    [Fact]
    public void Hold_AtFix_ClearsTheAssignedHeadingWhenItStarts()
    {
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(engine);

        AircraftState ac = AddAirborne(engine, "HCA01", GeoMath.ProjectPoint(FixPosition(), new TrueHeading(0), 3), "IFR");
        IssueVector(engine, ac);

        CommandResult result = engine.SendCommand(ac.Callsign, HoldCommand());
        Assert.True(result.Success, result.Message);
        Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);

        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }

    [Theory]
    [InlineData("HPPL")]
    [InlineData("HPPR")]
    public void HoldPresentPosition_ClearsTheAssignedHeading(string command)
    {
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(engine);

        AircraftState ac = AddAirborne(engine, "HCA02", GeoMath.ProjectPoint(FixPosition(), new TrueHeading(0), 3), "VFR");
        IssueVector(engine, ac);

        CommandResult result = engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, result.Message);
        Assert.IsType<VfrHoldPhase>(ac.Phases?.CurrentPhase);

        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }

    [Theory]
    [InlineData("HFIXL")]
    [InlineData("HFIXR")]
    public void HoldAtFix_Vfr_ClearsTheAssignedHeading(string command)
    {
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(engine);

        AircraftState ac = AddAirborne(engine, "HCA03", GeoMath.ProjectPoint(FixPosition(), new TrueHeading(0), 3), "VFR");
        IssueVector(engine, ac);

        CommandResult result = engine.SendCommand(ac.Callsign, $"{command} {FixName}");
        Assert.True(result.Success, result.Message);
        Assert.IsType<VfrHoldPhase>(ac.Phases?.CurrentPhase);

        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void QueuedHold_KeepsTheVectorUntilTheHoldStarts()
    {
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(engine);

        AircraftState ac = AddAirborne(engine, "HCA04", GeoMath.ProjectPoint(FixPosition(), new TrueHeading(0), 3), "IFR");

        // The hold is chained behind the vector, so it fires only once the aircraft has settled on 090.
        CommandResult result = engine.SendCommand(ac.Callsign, $"FH 090; {HoldCommand()}");
        Assert.True(result.Success, result.Message);

        int ticksBeforeHold = 0;
        for (int t = 1; t <= MaxTicks; t++)
        {
            if (ac.Phases?.CurrentPhase is HoldingPatternPhase)
            {
                break;
            }

            Assert.Equal(AssignedDegrees, ac.Targets.AssignedMagneticHeading?.Degrees);
            engine.TickOneSecond();
            ticksBeforeHold++;
        }

        Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(ticksBeforeHold > 1, $"the hold started on tick {ticksBeforeHold}, before the vector had settled");
        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void Capp_AfterAHold_IsNotTreatedAsOnAVector()
    {
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(engine);

        AircraftState ac = AddAirborne(engine, "HCA05", GeoMath.ProjectPoint(FixPosition(), new TrueHeading(0), 3), "IFR");
        IssueVector(engine, ac);

        CommandResult hold = engine.SendCommand(ac.Callsign, HoldCommand());
        Assert.True(hold.Success, hold.Message);

        // The hold drops its route once it reaches the fix; from then on only the stale vector could make a bare
        // CAPP look like a vectored intercept (ApproachCommandHandler's implied-PTAC path).
        bool reachedFix = false;
        for (int t = 1; t <= MaxTicks && !reachedFix; t++)
        {
            engine.TickOneSecond();
            reachedFix = (ac.Phases?.CurrentPhase is HoldingPatternPhase) && (ac.Targets.NavigationRoute.Count == 0);
        }
        Assert.True(reachedFix, "the aircraft never reached the hold fix");

        CommandResult capp = engine.SendCommand(ac.Callsign, "CAPP I28R");
        Assert.True(capp.Success, capp.Message);
        Assert.NotNull(ac.Phases);
        Assert.Contains(ac.Phases.Phases, p => p is ApproachNavigationPhase);
        Assert.DoesNotContain(ac.Phases.Phases, p => p is InterceptCoursePhase);
    }

    [Fact]
    public void Capp_WhileFlyingToTheHoldFix_TakesOverTheApproachAtTheFix()
    {
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(engine);

        (string fixName, LatLon fixPosition, List<string> transitionFixes) = TransitionOfI28R();
        AircraftState ac = AddAirborne(engine, "HCA06", GeoMath.ProjectPoint(fixPosition, new TrueHeading(0), 3), "IFR");
        IssueVector(engine, ac);

        CommandResult hold = engine.SendCommand(ac.Callsign, $"HOLDP {fixName} {InboundCourse} 1M R");
        Assert.True(hold.Success, hold.Message);
        Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);

        // The clearance arrives while the aircraft is still navigating to the hold fix: it then flies the approach's
        // transition and common legs as a route with the clearance pending, as a STAR arrival does, instead of
        // entering the hold (7110.65 §4-8-1.b.1; AIM 5-4-9).
        CommandResult capp = engine.SendCommand(ac.Callsign, "CAPP I28R");
        Assert.True(capp.Success, capp.Message);

        Assert.False(HasHoldingPhase(ac), "the hold is still installed after CAPP");
        Assert.NotNull(ac.Approach.PendingClearance);
        Assert.Equal("I28R", ac.Approach.PendingClearance.Clearance.ApproachId);

        int routeCount = ac.Targets.NavigationRoute.Count;
        Assert.True(routeCount >= 2, $"route after CAPP holds {routeCount} fix(es), expected the transition's fixes");
        int comparedFixes = Math.Min(transitionFixes.Count, routeCount);
        Assert.Equal(transitionFixes.Take(comparedFixes), ac.Targets.NavigationRoute.Take(comparedFixes).Select(n => n.Name));

        // Past the fix the route has moved on to the next transition fix: the aircraft leaves the fix on the approach.
        bool sequencedFix = false;
        for (int t = 1; t <= MaxTicks && !sequencedFix; t++)
        {
            engine.TickOneSecond();
            sequencedFix = (ac.Targets.NavigationRoute.Count > 0) && !IsFix(ac.Targets.NavigationRoute[0], fixName);
        }
        Assert.True(sequencedFix, "the route never moved past the hold fix");
        Assert.Equal(transitionFixes[1], ac.Targets.NavigationRoute[0].Name);
        Assert.False(HasHoldingPhase(ac), "a hold reappeared past the fix");
    }

    /// <summary>True when the aircraft carries a <see cref="HoldingPatternPhase"/>, IFR or hold-in-lieu.</summary>
    private static bool HasHoldingPhase(AircraftState ac) => ac.Phases?.Phases.Any(p => p is HoldingPatternPhase) == true;

    private static bool IsFix(NavigationTarget target, string fixName) => string.Equals(target.Name, fixName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A real I28R transition at KOAK whose connecting fix is not a common-leg fix and whose legs carry no course
    /// reversal, with that fix's position (where a hold is issued) and the transition's fixes in published order.
    /// </summary>
    private static (string FixName, LatLon FixPosition, List<string> Fixes) TransitionOfI28R()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(navDb.GetApproach("OAK", "I28R"));

        foreach (CifpTransition transition in procedure.Transitions.Values)
        {
            List<string> fixes = [.. transition.Legs.Select(leg => leg.FixIdentifier).Where(name => !string.IsNullOrEmpty(name))];
            if (fixes.Count < 2)
            {
                continue;
            }

            string connectingFix = fixes[0];
            bool inCommonLegs = procedure.CommonLegs.Any(leg => string.Equals(leg.FixIdentifier, connectingFix, StringComparison.OrdinalIgnoreCase));
            bool hasCourseReversal = transition.Legs.Any(leg =>
                leg.PathTerminator is CifpPathTerminator.PI or CifpPathTerminator.HM or CifpPathTerminator.HF or CifpPathTerminator.HA
            );
            if (inCommonLegs || hasCourseReversal)
            {
                continue;
            }

            if (navDb.GetFixPosition(connectingFix) is { } position)
            {
                return (connectingFix, new LatLon(position.Lat, position.Lon), fixes);
            }
        }

        throw new InvalidOperationException("I28R at OAK has no transition fix usable from a hold");
    }
}

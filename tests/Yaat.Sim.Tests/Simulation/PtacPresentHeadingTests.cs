using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// PTAC with <c>PH</c> means "no new vector" (7110.65 §5-9-4): while the aircraft is still turning
/// to its last assigned heading, that heading stays in force, so <c>PTAC PH</c> must intercept on
/// it and keep the turn direction it was given. Once the vector is behind the aircraft — a hold,
/// procedure or pattern owns the heading — PH takes the present heading. With no assigned heading
/// in force (direct, route, procedure) PH does the same. Real KOAK navdata and the real I28R
/// approach; the aircraft is a B738 southwest of the field at 3,000 ft, and the hold case starts it
/// at GROVE, a fix on the approach, so the hold reaches its route-empty outbound segment.
/// </summary>
public class PtacPresentHeadingTests
{
    private const string Callsign = "N917YA";
    private const string ApproachId = "I28R";
    private const string Airport = "OAK";
    private const double StartLat = 37.68;
    private const double StartLon = -122.42;
    private const int StartAltitudeFt = 3000;

    private readonly ITestOutputHelper _output;

    public PtacPresentHeadingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void PtacPh_MidTurnToAssignedHeading_UsesAssignedHeading()
    {
        if (BuildHarness(magneticHeading: 200) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult fh = Send(harness, "FH 250");
        Assert.True(fh.Success, fh.Message);

        TickUntil(harness, ac => ac.MagneticHeading.Degrees is > 210 and < 240, 30);
        double inBetween = harness.Aircraft.MagneticHeading.Degrees;
        _output.WriteLine($"Mid-turn heading {inBetween:F1}");
        Assert.InRange(inBetween, 210, 240);

        CommandResult ptac = Send(harness, "PTAC PH PA I28R");
        _output.WriteLine($"PTAC PH -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Equal(250.0, AssignedMagneticHeading(harness));
        PhaseList phases = Assert.IsType<PhaseList>(harness.Aircraft.Phases);
        InterceptCoursePhase intercept = Assert.Single(phases.Phases.OfType<InterceptCoursePhase>());
        MagneticHeading assignedIntercept = Assert.NotNull(intercept.AssignedInterceptHeading);
        Assert.Equal(250.0, assignedIntercept.Degrees);
        Assert.Contains("Fly heading 250", ptac.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PtacPh_LongWayTurn_KeepsTurnDirection()
    {
        if (BuildHarness(magneticHeading: 300) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult tr = Send(harness, "TR 250");
        Assert.True(tr.Success, tr.Message);
        Assert.Equal(TurnDirection.Right, harness.Aircraft.Targets.PreferredTurnDirection);

        TickUntil(harness, ac => ac.MagneticHeading.Degrees is > 310 and < 350, 40);
        double inBetween = harness.Aircraft.MagneticHeading.Degrees;
        _output.WriteLine($"Mid-turn heading {inBetween:F1} (still turning right the long way to 250)");
        Assert.InRange(inBetween, 310, 350);
        Assert.Equal(TurnDirection.Right, harness.Aircraft.Targets.PreferredTurnDirection);

        CommandResult ptac = Send(harness, "PTAC PH PA I28R");
        _output.WriteLine($"PTAC PH -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Equal(TurnDirection.Right, harness.Aircraft.Targets.PreferredTurnDirection);
        Assert.Equal(250.0, AssignedMagneticHeading(harness));
        Assert.Contains("Fly heading 250", ptac.Message, StringComparison.Ordinal);

        double beforeTick = harness.Aircraft.MagneticHeading.Degrees;
        for (int t = 0; t < 5; t++)
        {
            harness.Engine.TickOneSecond();
        }

        double afterTick = harness.Aircraft.MagneticHeading.Degrees;
        double turned = WrappedDelta(afterTick - beforeTick);
        _output.WriteLine($"After PTAC PH: {beforeTick:F1} -> {afterTick:F1} ({turned:+0.0;-0.0}°)");
        Assert.True(turned > 0, $"PTAC PH must keep the aircraft turning right; it moved {turned:F1}° from {beforeTick:F1} to {afterTick:F1}");
    }

    [Fact]
    public void PtacPh_OnDirectToFix_UsesPresentHeading()
    {
        if (BuildHarness(magneticHeading: 200) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        string fix = ApproachFixName();
        CommandResult dct = Send(harness, $"DCT {fix}");
        Assert.True(dct.Success, dct.Message);
        Assert.NotEmpty(harness.Aircraft.Targets.NavigationRoute);

        for (int t = 0; t < 5; t++)
        {
            harness.Engine.TickOneSecond();
        }

        int present = (int)Math.Round(harness.Aircraft.MagneticHeading.Degrees);
        _output.WriteLine($"On direct {fix} at present heading {present:000}");

        CommandResult ptac = Send(harness, "PTAC PH PA I28R");
        _output.WriteLine($"PTAC PH -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Empty(harness.Aircraft.Targets.NavigationRoute);
        Assert.Equal(present, (int)Math.Round(AssignedMagneticHeading(harness)));
        Assert.Contains("Fly heading", ptac.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PtacPh_RouteWithStaleAssignedHeading_UsesPresentHeading()
    {
        if (BuildHarness(magneticHeading: 200) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        string fix = ApproachFixName();
        CommandResult dct = Send(harness, $"DCT {fix}");
        Assert.True(dct.Success, dct.Message);
        Assert.NotEmpty(harness.Aircraft.Targets.NavigationRoute);

        harness.Aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(310);
        harness.Aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
        int present = (int)Math.Round(harness.Aircraft.MagneticHeading.Degrees);
        _output.WriteLine($"Stale assigned heading 310, present heading {present:000}");

        CommandResult ptac = Send(harness, "PTAC PH PA I28R");
        _output.WriteLine($"PTAC PH -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        int heading = (int)Math.Round(AssignedMagneticHeading(harness));
        Assert.NotEqual(310, present);
        Assert.Equal(present, heading);
        Assert.NotEqual(310, heading);
        Assert.Null(harness.Aircraft.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PtacPh_InHoldAfterAVector_UsesPresentHeading()
    {
        string fix = ApproachFixName();
        (double Lat, double Lon) fixPosition = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition(fix));
        var start = new LatLon(fixPosition.Lat, fixPosition.Lon);
        if (BuildHarness(magneticHeading: 200, position: start) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult fh = Send(harness, "FH 250");
        Assert.True(fh.Success, fh.Message);
        for (int t = 0; t < 3; t++)
        {
            harness.Engine.TickOneSecond();
        }

        CommandResult hold = Send(harness, $"HOLD {fix} 130 2 RIGHT");
        _output.WriteLine($"HOLD {fix} -> {hold.Message}");
        Assert.True(hold.Success, hold.Message);

        // YAAT-951: the hold clears the cancelled vector at its start, so PTAC PH here takes the present heading.

        TickUntil(
            harness,
            ac =>
                (ac.Phases?.CurrentPhase is HoldingPatternPhase)
                && (ac.Targets.NavigationRoute.Count == 0)
                && (AngleFrom(ac.MagneticHeading.Degrees, 250) > 30),
            180
        );

        AircraftState holding = harness.Aircraft;
        double holdHeading = holding.MagneticHeading.Degrees;
        _output.WriteLine(
            $"In hold: phase={holding.Phases?.CurrentPhase?.Name} hdg={holdHeading:F1} route={holding.Targets.NavigationRoute.Count} "
                + $"assigned={holding.Targets.AssignedMagneticHeading?.Degrees:F0} targetTrue={holding.Targets.TargetTrueHeading?.Degrees:F0}"
        );
        Assert.IsType<HoldingPatternPhase>(holding.Phases?.CurrentPhase);
        Assert.Empty(holding.Targets.NavigationRoute);
        Assert.Null(holding.Targets.AssignedMagneticHeading);
        Assert.True(
            AngleFrom(holdHeading, 250) > 30,
            $"Expected the aircraft flying the hold, away from the 250 vector; heading was {holdHeading:F1}"
        );

        int present = (int)Math.Round(holdHeading);
        CommandResult ptac = Send(harness, "PTAC PH PA I28R");
        _output.WriteLine($"PTAC PH -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Equal(present, (int)Math.Round(AssignedMagneticHeading(harness)));
        Assert.Null(harness.Aircraft.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PtacfPh_MidTurn_SaysForceFly()
    {
        if (BuildHarness(magneticHeading: 200) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult fh = Send(harness, "FH 250");
        Assert.True(fh.Success, fh.Message);
        TickUntil(harness, ac => ac.MagneticHeading.Degrees is > 210 and < 240, 30);
        Assert.InRange(harness.Aircraft.MagneticHeading.Degrees, 210, 240);

        CommandResult ptacf = Send(harness, "PTACF PH PA I28R");
        _output.WriteLine($"PTACF PH -> {ptacf.Message}");
        Assert.True(ptacf.Success, ptacf.Message);

        Assert.Equal(250.0, AssignedMagneticHeading(harness));
        Assert.StartsWith("Force: fly heading 250", ptacf.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyPresentHeading_MidTurn_StillFreezesTheInBetweenHeading()
    {
        if (BuildHarness(magneticHeading: 200) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult fh = Send(harness, "FH 250");
        Assert.True(fh.Success, fh.Message);

        TickUntil(harness, ac => ac.MagneticHeading.Degrees is > 210 and < 240, 30);
        double inBetween = harness.Aircraft.MagneticHeading.Degrees;
        _output.WriteLine($"Mid-turn heading {inBetween:F1}");
        Assert.InRange(inBetween, 210, 240);

        CommandResult fph = Send(harness, "FPH");
        Assert.True(fph.Success, fph.Message);

        double assigned = AssignedMagneticHeading(harness);
        _output.WriteLine($"FPH -> {fph.Message} (assigned {assigned:F1})");
        Assert.Equal((int)Math.Round(inBetween), (int)Math.Round(assigned));
        Assert.NotEqual(250, (int)Math.Round(assigned));
    }

    [Fact]
    public void PtacExplicitHeading_MessageStillSaysTurn()
    {
        if (BuildHarness(magneticHeading: 200) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult ptac = Send(harness, "PTAC 280 PA I28R");
        _output.WriteLine($"PTAC 280 -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Equal(280.0, AssignedMagneticHeading(harness));
        Assert.StartsWith("Turn heading 280", ptac.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PTAC heading with a turn direction (7110.65 5-6-2.a.1) is flown that way even when it is the long way round: from
    /// 270, "turn right heading 240" turns through north rather than 30 degrees left.
    /// </summary>
    [Fact]
    public void Ptac_WithRightTurn_TurnsTheLongWayRight()
    {
        if (BuildHarness(magneticHeading: 270) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult ptac = Send(harness, "PTAC R240 030 ILS28R");
        _output.WriteLine($"PTAC R240 -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Equal(TurnDirection.Right, harness.Aircraft.Targets.PreferredTurnDirection);
        Assert.Equal(240.0, AssignedMagneticHeading(harness));
        Assert.StartsWith("Turn right heading 240", ptac.Message, StringComparison.Ordinal);

        double beforeTick = harness.Aircraft.MagneticHeading.Degrees;
        for (int t = 0; t < 5; t++)
        {
            harness.Engine.TickOneSecond();
        }

        double afterTick = harness.Aircraft.MagneticHeading.Degrees;
        double turned = WrappedDelta(afterTick - beforeTick);
        _output.WriteLine($"After PTAC R240: {beforeTick:F1} -> {afterTick:F1} ({turned:+0.0;-0.0}°)");
        Assert.True(turned > 0, $"PTAC R240 must turn right through north; it moved {turned:F1}° from {beforeTick:F1} to {afterTick:F1}");
        Assert.InRange(afterTick, 270.5, 360);
    }

    [Fact]
    public void Ptac_WithLeftTurn_SetsLeftAndSaysTurnLeft()
    {
        if (BuildHarness(magneticHeading: 270) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult ptac = Send(harness, "PTAC L240 030 ILS28R");
        _output.WriteLine($"PTAC L240 -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Equal(TurnDirection.Left, harness.Aircraft.Targets.PreferredTurnDirection);
        Assert.StartsWith("Turn left heading 240", ptac.Message, StringComparison.Ordinal);
    }

    /// <summary>A PTAC heading with no direction is flown the shorter way: it clears a turn direction an earlier vector gave.</summary>
    [Fact]
    public void Ptac_WithoutTurn_ClearsTheEarlierTurnDirection()
    {
        if (BuildHarness(magneticHeading: 270) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        CommandResult tr = Send(harness, "TR 200");
        Assert.True(tr.Success, tr.Message);
        Assert.Equal(TurnDirection.Right, harness.Aircraft.Targets.PreferredTurnDirection);

        CommandResult ptac = Send(harness, "PTAC 240 030 ILS28R");
        _output.WriteLine($"PTAC 240 -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        Assert.Null(harness.Aircraft.Targets.PreferredTurnDirection);
        Assert.StartsWith("Turn heading 240", ptac.Message, StringComparison.Ordinal);
    }

    private static double AssignedMagneticHeading(Harness harness)
    {
        MagneticHeading assigned = Assert.NotNull(harness.Aircraft.Targets.AssignedMagneticHeading);
        return assigned.Degrees;
    }

    private static CommandResult Send(Harness harness, string command) => harness.Engine.SendCommand(Callsign, command);

    private static void TickUntil(Harness harness, Func<AircraftState, bool> done, int maxSeconds)
    {
        for (int t = 0; t < maxSeconds; t++)
        {
            harness.Engine.TickOneSecond();
            if (done(harness.Aircraft))
            {
                return;
            }
        }
    }

    /// <summary>Signed shortest difference between two headings, in (-180, 180].</summary>
    private static double WrappedDelta(double degrees)
    {
        double d = degrees % 360;
        if (d > 180)
        {
            d -= 360;
        }

        if (d < -180)
        {
            d += 360;
        }

        return d;
    }

    private static double AngleFrom(double heading, double reference) => Math.Abs(WrappedDelta(heading - reference));

    /// <summary>
    /// A real fix on the I28R approach side that <c>DCT</c> and <c>HOLD</c> can resolve, taken from
    /// the procedure's own legs so the name cannot go stale against a nav cycle.
    /// </summary>
    private static string ApproachFixName()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string resolvedId = Assert.IsType<string>(navDb.ResolveApproachId(Airport, ApproachId));
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(navDb.GetApproach(Airport, resolvedId));
        CifpLeg leg = Assert.IsType<CifpLeg>(procedure.CommonLegs.FirstOrDefault(l => IsResolvableApproachFix(navDb, l)));
        return leg.FixIdentifier;
    }

    private static bool IsResolvableApproachFix(NavigationDatabase navDb, CifpLeg leg)
    {
        string name = leg.FixIdentifier;
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("RW", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return navDb.GetFixPosition(name) is not null;
    }

    private Harness? BuildHarness(double magneticHeading) => BuildHarness(magneticHeading, new LatLon(StartLat, StartLon));

    private Harness? BuildHarness(double magneticHeading, LatLon position)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout(Airport);
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        double declination = MagneticDeclination.GetDeclination(position, MagneticDeclination.EvaluationDateUtc);
        TrueHeading trueHeading = new MagneticHeading(magneticHeading).ToTrue(declination);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = trueHeading,
            TrueTrack = trueHeading,
            Altitude = StartAltitudeFt,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            Declination = declination,
            DeclinationCachePosition = position,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = Airport,
                Destination = Airport,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(StartAltitudeFt),
            },
            Phases = new PhaseList(),
        };
        aircraft.Ground.Layout = layout;

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "ptac-present-heading",
            ScenarioName = "PTAC present heading",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = Airport,
        };

        _output.WriteLine($"Start: magnetic {aircraft.MagneticHeading.Degrees:F1}, declination {declination:F1}");
        return new Harness(engine, aircraft);
    }

    private sealed record Harness(SimulationEngine Engine, AircraftState Aircraft);
}

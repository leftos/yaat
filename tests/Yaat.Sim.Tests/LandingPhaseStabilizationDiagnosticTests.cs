using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies the per-condition diagnostic emitted by LandingPhase's stabilization gate.
/// The gate fires when one or more of (IAS &gt; 1.3·Vref, |XTE| &gt; 0.08 NM, |Bank| &gt; 15°,
/// VS &lt; -1200 fpm) holds for ≥ 1 second of grace. The warning text must spell out the
/// specific failed condition(s) so RPOs/instructors can debrief from the message alone
/// instead of replaying the bundle.
///
/// Drives the simulation through SimulationWorld.Tick + a preTick callback that runs
/// PhaseRunner.Tick — same path SimulationEngine.TickPhysics uses in production.
/// </summary>
public class LandingPhaseStabilizationDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public LandingPhaseStabilizationDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static RunwayInfo MakeRunway() =>
        TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 0, thresholdLat: 37.0, thresholdLon: -122.0);

    private static AircraftState MakeAircraftOnFinal(RunwayInfo rwy, double agl, double ias, double bank, double vs, double xteNm)
    {
        // Place aircraft on final 1 nm out, perpendicularly offset by xteNm from centerline.
        var (alongLat, alongLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 1.0);
        var perp = rwy.TrueHeading + 90.0; // right of runway = positive XTE
        var (offsetLat, offsetLon) = GeoMath.ProjectPoint(alongLat, alongLon, perp, xteNm);

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(offsetLat, offsetLon),
            TrueHeading = rwy.TrueHeading,
            Altitude = agl,
            IndicatedAirspeed = ias,
            BankAngle = bank,
            VerticalSpeed = vs,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST" },
        };

        ac.Phases = new PhaseList { AssignedRunway = rwy, LandingClearance = ClearanceType.ClearedToLand };
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = ias;
        return ac;
    }

    private static void PreTick(AircraftState aircraft, double dt)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return;
        }

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = dt,
            Runway = aircraft.Phases.AssignedRunway,
            FieldElevation = aircraft.Phases.AssignedRunway?.ElevationFt ?? 0,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
        };
        PhaseRunner.Tick(aircraft, ctx);
    }

    /// <summary>
    /// Tick the world for up to <paramref name="maxSeconds"/> seconds of simulated time
    /// at production cadence (4 Hz), draining warnings each tick. Returns the first
    /// "going around" warning encountered, or null if none fired.
    /// </summary>
    private static string? RunUntilGoAround(SimulationWorld world, int maxSeconds = 10)
    {
        const double dt = 0.25;
        int maxTicks = maxSeconds * 4;
        for (int i = 0; i < maxTicks; i++)
        {
            world.Tick(dt, PreTick);
            foreach (var (_, warning) in world.DrainAllWarnings())
            {
                if (warning.Contains("going around", StringComparison.OrdinalIgnoreCase))
                {
                    return warning;
                }
            }
        }
        return null;
    }

    [Fact]
    public void Reason_LeadsWith_unstable_NotUnstabilized()
    {
        var rwy = MakeRunway();
        var ac = MakeAircraftOnFinal(rwy, agl: 200, ias: 200, bank: 0, vs: -700, xteNm: 0);
        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var warning = RunUntilGoAround(world);
        Assert.NotNull(warning);
        _output.WriteLine(warning);
        Assert.Contains("unstable:", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("unstabilized", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_OverspeedAboveOnePointThreeVref_NamesIas()
    {
        var rwy = MakeRunway();
        // B738 Vref ~140; 1.3·Vref = 182. IAS=200 trips speed gate only.
        var ac = MakeAircraftOnFinal(rwy, agl: 200, ias: 200, bank: 0, vs: -700, xteNm: 0);
        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var warning = RunUntilGoAround(world);
        Assert.NotNull(warning);
        _output.WriteLine(warning);
        Assert.Contains("IAS", warning, StringComparison.Ordinal);
        Assert.Contains("1.3·Vref", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_OffCenterlineXte_NamesFeet()
    {
        var rwy = MakeRunway();
        // 0.15 nm = ~911 ft, comfortably above 0.08 nm threshold.
        var ac = MakeAircraftOnFinal(rwy, agl: 200, ias: 140, bank: 0, vs: -700, xteNm: 0.15);
        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var warning = RunUntilGoAround(world);
        Assert.NotNull(warning);
        _output.WriteLine(warning);
        Assert.Contains("off centerline", warning, StringComparison.Ordinal);
        Assert.Contains("ft", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_HighBank_NamesBankDegrees()
    {
        var rwy = MakeRunway();
        // Force a bank by setting the aircraft heading 10° off runway centerline.
        // LandingPhase's TickStabilizedApproach will demand runway heading; FlightPhysics
        // turns the aircraft at standard rate, producing a ~22° bank that persists until
        // the turn completes (~3 sec at dt=0.25). Co-fires with the descent gate (Landing
        // descends at full default rate from 100 ft AGL since TickStabilizedApproach
        // doesn't override DescentRate) — that's expected; multi-condition formatting is
        // covered by Reason_MultipleConditions_JoinsWithComma.
        var ac = MakeAircraftOnFinal(rwy, agl: 100, ias: 140, bank: 0, vs: 0, xteNm: 0);
        ac.TrueHeading = rwy.TrueHeading + 10.0;
        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var warning = RunUntilGoAround(world);
        Assert.NotNull(warning);
        _output.WriteLine(warning);
        Assert.Contains("bank", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("°", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_HighDescent_NamesFpm()
    {
        var rwy = MakeRunway();
        // FlightPhysics recomputes VS from DesiredVerticalRate (or default DescentRate)
        // each tick — pre-seeding aircraft.VerticalSpeed alone wouldn't survive the first
        // integration. Set DVR=-1500 so FlightPhysics writes VS=-1500 every tick.
        var ac = MakeAircraftOnFinal(rwy, agl: 200, ias: 140, bank: 0, vs: -1500, xteNm: 0);
        ac.Targets.DesiredVerticalRate = -1500;
        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var warning = RunUntilGoAround(world);
        Assert.NotNull(warning);
        _output.WriteLine(warning);
        Assert.Contains("descent", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fpm", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_MultipleConditions_JoinsWithComma()
    {
        var rwy = MakeRunway();
        // Three failures simultaneously: IAS, XTE, descent.
        var ac = MakeAircraftOnFinal(rwy, agl: 200, ias: 200, bank: 0, vs: -1500, xteNm: 0.15);
        ac.Targets.DesiredVerticalRate = -1500; // force FlightPhysics to keep VS at -1500 fpm
        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var warning = RunUntilGoAround(world);
        Assert.NotNull(warning);
        _output.WriteLine(warning);
        // Comma separates each clause.
        Assert.True(warning.Count(c => c == ',') >= 2, $"Expected multiple comma-separated clauses, got: {warning}");
        Assert.Contains("IAS", warning, StringComparison.Ordinal);
        Assert.Contains("off centerline", warning, StringComparison.Ordinal);
        Assert.Contains("descent", warning, StringComparison.OrdinalIgnoreCase);
    }
}

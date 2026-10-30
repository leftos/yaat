using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The altitude hold and the bare go-around refusal measure "inside the final approach fix" against the published FAF,
/// uncapped: on KSFO ILS 28L (FAF DUYET, more than 5.5 nm out) an aircraft between five miles and DUYET is inside it. The
/// real CIFP is parsed once and scoped into <see cref="ApproachGateDatabase"/> for each test. Real KSFO navdata and CIFP.
/// </summary>
public class ApproachPublishedFafTests(ITestOutputHelper output)
{
    private const string Callsign = "APCH1";

    /// <summary>The real CIFP, parsed once for every test that scopes it into <see cref="ApproachGateDatabase"/>.</summary>
    internal static readonly Lazy<CifpParseResult> Cifp = new(() =>
    {
        TestVnasData.EnsureInitialized();
        return CifpParser.Parse(TestVnasData.GetCifpPath()!);
    });

    internal static RunwayInfo Sfo28L()
    {
        TestVnasData.EnsureInitialized();
        return NavigationDatabase.Instance.GetRunway("KSFO", "28L") ?? throw new InvalidOperationException("KSFO 28L missing from navdata");
    }

    /// <summary>KSFO 28L's published FAF distance from the scoped table, asserted beyond 5.5 nm so FAF − 0.5 nm is past five miles.</summary>
    internal static double PublishedFafNm()
    {
        double? fafNm = ApproachGateDatabase.GetFafDistanceNm("KSFO", "28L", 0);
        Assert.NotNull(fafNm);
        Assert.True(fafNm > 5.5, $"KSFO 28L published FAF is {fafNm:F2} nm, not beyond 5.5 nm");
        return fafNm.Value;
    }

    /// <summary>A point on the 28L extended centerline, <paramref name="distanceNm"/> from the threshold.</summary>
    internal static LatLon OnCenterline(RunwayInfo rwy, double distanceNm) =>
        GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), distanceNm);

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// An IFR B738 cleared for the KSFO ILS 28L, its chain advanced onto the final, then placed <paramref name="distanceNm"/> out
    /// on the centerline at <paramref name="altitude"/>.
    /// </summary>
    internal static AircraftState OnFinal(SimulationEngine engine, double distanceNm, double altitude)
    {
        RunwayInfo rwy = Sfo28L();
        var ac = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = OnCenterline(rwy, 12.0),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = 3500,
            IndicatedAirspeed = 170,
            IsOnGround = false,
            Declination = 13.0,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "KSFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(5000),
                CruiseSpeed = 250,
            },
            Procedure = new AircraftProcedure { DestinationRunway = null },
        };
        var capp = new ClearedApproachCommand("I28L", "KSFO", false, null, null, null, null, null, null, null, null);
        CommandResult cleared = ApproachCommandHandler.TryClearedApproach(capp, ac);
        Assert.True(cleared.Success, cleared.Message);
        engine.World.AddAircraft(ac);

        PhaseList phases = ac.Phases!;
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);
        if (phases.CurrentPhase is { Status: PhaseStatus.Pending })
        {
            phases.Start(ctx);
        }

        while ((phases.CurrentPhase is not null) && (phases.CurrentPhase is not FinalApproachPhase))
        {
            phases.AdvanceToNext(ctx);
        }

        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(phases.CurrentPhase);
        Assert.NotNull(phases.ActiveApproach);
        ac.Position = OnCenterline(rwy, distanceNm);
        final.DistanceToThresholdNm = distanceNm;
        ac.Altitude = altitude;
        return ac;
    }

    private CommandResult Dispatch(AircraftState ac, string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new SerializableRandom(42)));
        output.WriteLine($"{command} on {ac.Phases?.CurrentPhase?.GetType().Name}: success={result.Success} — {result.Message}");
        return result;
    }

    /// <summary>Between five miles and the published FAF a bare go-around is inside the FAF: the missed approach is flown.</summary>
    [Fact]
    public void BareGoAround_BetweenFiveMilesAndThePublishedFaf_FliesTheMissedApproach()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(Cifp.Value);
        AircraftState ac = OnFinal(BuildEngine(), PublishedFafNm() - 0.5, 3500);

        CommandResult result = Dispatch(ac, "GA");

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("unable", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.Contains(ac.Phases.Phases, p => p is ApproachNavigationPhase { IsMissedApproach: true });
    }

    /// <summary>Between five miles and the published FAF a DM is inside the FAF: it cancels the approach.</summary>
    [Fact]
    public void DescendMaintain_BetweenFiveMilesAndThePublishedFaf_CancelsTheApproach()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(Cifp.Value);
        AircraftState ac = OnFinal(BuildEngine(), PublishedFafNm() - 0.5, 3500);

        CommandResult result = Dispatch(ac, "DM 020");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.False(ac.Phases?.ActiveApproach is { AltitudeHeldUntilEstablished: true });
        Assert.Equal(2000, ac.Targets.AssignedAltitude);
    }

    /// <summary>Outside the published FAF a DM keeps the approach and is held until established.</summary>
    [Fact]
    public void DescendMaintain_OutsideThePublishedFaf_KeepsTheApproach()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(Cifp.Value);
        AircraftState ac = OnFinal(BuildEngine(), PublishedFafNm() + 1.0, 3500);
        ApproachClearance approach = ac.Phases!.ActiveApproach!;

        CommandResult result = Dispatch(ac, "DM 030");

        Assert.True(result.Success, result.Message);
        Assert.Same(approach, ac.Phases!.ActiveApproach);
        Assert.True(approach.AltitudeHeldUntilEstablished);
        Assert.Equal(3000, ac.Targets.AssignedAltitude);
    }
}

using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// A LAHSO stop leaves the aircraft on the runway short of the hold-short point, so it holds there (AIM 4-3-11.b.6)
/// whatever its phase chain had queued after the landing. <see cref="PhaseRunner"/> ends the phase the advance just
/// started, drops the rest, and installs the runway-hold chain it installs when the landing was the last phase.
/// </summary>
public class PhaseRunnerLahsoPendingChainTests(ITestOutputHelper output)
{
    private static readonly TestAirportGroundData GroundData = new();

    /// <summary>
    /// SFO 28R on 1 nm final, cleared to land and hold short of 1L, with a phase queued behind the landing. A B738
    /// has no exit before the 1L hold-short point and stops for LAHSO (see <c>LahsoRolloutTests</c>).
    /// </summary>
    [Fact]
    public void LahsoStop_WithPendingPhaseAfterLanding_HoldsOnRunway()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        RunwayInfo? runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);
        var aircraft = new AircraftState
        {
            Callsign = "TST001",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.ElevationFt + 318,
            IndicatedAirspeed = 145,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway28R },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));

        var engine = new SimulationEngine(GroundData);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-lahso-pending",
            ScenarioName = "SFO 28R LAHSO pending chain",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        CommandResult lahsoResult = engine.SendCommand("TST001", "LAHSO 1L");
        Assert.True(lahsoResult.Success, $"LAHSO 1L failed: {lahsoResult.Message}");

        var placeholder = new HoldingInPositionPhase();
        aircraft.Phases.Add(placeholder);
        LandingPhase landing = aircraft.Phases.Phases.OfType<LandingPhase>().Single();

        for (int t = 1; (t <= 420) && (landing.Status != PhaseStatus.Completed); t++)
        {
            engine.TickOneSecond();
        }

        List<Phase> chain = aircraft.Phases.Phases;
        output.WriteLine(
            $"current={aircraft.Phases.CurrentPhase?.Name ?? "none"} index={aircraft.Phases.CurrentIndex} "
                + $"chain=[{string.Join(", ", chain.Select(p => $"{p.Name}:{p.Status}"))}]"
        );

        Assert.Equal(PhaseStatus.Completed, landing.Status);
        Assert.True(landing.StoppedForLahso, "the B738 should have completed its landing as a LAHSO stop");

        Assert.IsType<RunwayHoldingPhase>(aircraft.Phases.CurrentPhase);

        // The queued phase was ended as skipped and is behind the current index: it never runs.
        Assert.Equal(PhaseStatus.Skipped, placeholder.Status);
        Assert.True(chain.IndexOf(placeholder) < aircraft.Phases.CurrentIndex, "the dropped phase must not be ahead of the runway hold");

        // The upcoming chain is exactly the one a LAHSO stop with nothing queued gets.
        List<Type> expected = [typeof(RunwayHoldingPhase), typeof(RunwayExitPhase), typeof(HoldingAfterExitPhase)];
        List<Type> upcoming = [.. chain.Skip(aircraft.Phases.CurrentIndex).Select(p => p.GetType())];
        Assert.Equal(expected, upcoming);
        Assert.Null(aircraft.Phases.LahsoHoldShort);
    }
}

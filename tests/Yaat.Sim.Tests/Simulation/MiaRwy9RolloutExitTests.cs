using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression for "aircraft landing KMIA runway 9 roll the full 13,000 ft instead of
/// exiting at a mid-field taxiway, even narrowbodies like a B737."
///
/// KMIA runway 9/27 authors <c>"turnoff": "left"</c> in its GeoJSON. Runway 9 has nine
/// left-side exits spread along its length but only one right-side exit (Q10). The runtime
/// feeds the zero-padded identity <c>"09"</c> to the exit search, but the authored turnoff
/// was looked up through a leading-zero gap: <see cref="AirportGroundLayout.FindRunway"/>
/// split the GeoJSON name <c>"9 - 27"</c> into <c>["9","27"]</c> and compared raw, so
/// <c>FindRunway("09")</c> returned null and the authored side was dropped. The exit-side
/// heuristic then picked Right (parking proximity override), stranding the aircraft with a
/// single reachable exit and a full-length rollout.
/// </summary>
public class MiaRwy9RolloutExitTests(ITestOutputHelper output)
{
    private const string Callsign = "AAL738";

    /// <summary>
    /// Sharpest probe: the authored <c>turnoff: left</c> must be honored for the zero-padded
    /// runtime designator "09" exactly as it is for the de-padded "9". Before the fix,
    /// "09" resolves to Right (heuristic) while "9" resolves to Left (authored) — a flip.
    /// </summary>
    [Fact]
    public void InferPreferredExitSide_Rwy9_HonorsAuthoredLeftTurnoff_ForPaddedDesignator()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("MIA");
        if (layout is null)
        {
            return;
        }

        var rwy9 = NavigationDatabase.Instance.GetRunway("MIA", "9");
        Assert.NotNull(rwy9);
        Assert.Equal("09", rwy9.Designator);

        var sidePadded = layout.InferPreferredExitSide("09", rwy9.TrueHeading);
        var sideUnpadded = layout.InferPreferredExitSide("9", rwy9.TrueHeading);
        output.WriteLine($"InferPreferredExitSide: 09 -> {sidePadded}, 9 -> {sideUnpadded} (authored turnoff = left)");

        Assert.Equal(ExitSide.Left, sidePadded);
        Assert.Equal(sideUnpadded, sidePadded);
    }

    /// <summary>
    /// End-to-end: a B738 on 1 nm final to runway 9 with no exit instruction must commit to a
    /// mid-field exit well short of the 13,000 ft departure end — not roll the whole runway.
    /// </summary>
    [Fact]
    public void B738_LandingRwy9_NoExitInstruction_ExitsMidfield_NotFullRollout()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var rwy9 = navDb.GetRunway("MIA", "9");
        if (rwy9 is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("MIA");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var engine = new SimulationEngine(new TestAirportGroundData());

        double reciprocal = (rwy9.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(rwy9.ThresholdLatitude, rwy9.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = rwy9.TrueHeading,
            Altitude = rwy9.ElevationFt + 318,
            IndicatedAirspeed = 145,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "MIA",
                Destination = "MIA",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = rwy9 };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-mia-rwy9-exit",
            ScenarioName = "MIA Rwy 9 Exit Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "MIA",
        };

        var clear = engine.SendCommand(Callsign, "CLAND");
        Assert.True(clear.Success, $"CLAND failed: {clear.Message}");

        var threshold = new LatLon(rwy9.ThresholdLatitude, rwy9.ThresholdLongitude);
        string? exitTaxiway = null;
        double exitCommitDistFt = -1;
        double maxAlongFt = 0;

        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "none";
            double alongFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, rwy9.TrueHeading) * 6076.12;
            maxAlongFt = Math.Max(maxAlongFt, alongFt);

            if (aircraft.Ground.CurrentTaxiway is not null && exitTaxiway is null)
            {
                exitTaxiway = aircraft.Ground.CurrentTaxiway;
                exitCommitDistFt = alongFt;
                output.WriteLine(
                    $"t={t}: committed to exit {exitTaxiway} at {alongFt:F0} ft past threshold (gs={aircraft.GroundSpeed:F1}, phase={phase})"
                );
            }

            if ((aircraft.GroundSpeed <= 1.0) && (t > 30))
            {
                output.WriteLine($"t={t}: stopped at {alongFt:F0} ft, phase={phase}, taxiway={aircraft.Ground.CurrentTaxiway ?? "(none)"}");
                break;
            }
        }

        output.WriteLine(
            $"Summary: exit={exitTaxiway ?? "(none)"}, commitDist={exitCommitDistFt:F0} ft, "
                + $"maxAlong={maxAlongFt:F0} ft, runwayLen={rwy9.LengthFt:F0} ft"
        );

        Assert.NotNull(exitTaxiway);
        Assert.True(
            exitCommitDistFt < 8500,
            $"B738 landing MIA rwy 9 committed to exit {exitTaxiway} {exitCommitDistFt:F0} ft past threshold on a "
                + $"{rwy9.LengthFt:F0} ft runway — expected a mid-field exit, not a near-full rollout. Authored turnoff is 'left'."
        );
    }
}

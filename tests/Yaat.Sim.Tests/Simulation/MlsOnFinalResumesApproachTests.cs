using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: a manual S-turn for spacing (<c>MLS</c>/<c>MRS</c>) issued while the aircraft
/// is already on final must resume the approach after the S-turns, not skip straight to
/// landing.
///
/// <see cref="PatternCommandHandler.TryMakeSTurns"/> inserted the <see cref="STurnPhase"/>
/// after the current phase with no resume clone. When the current phase is
/// <see cref="FinalApproachPhase"/>, advancing past it dropped the chain to
/// <c>... STurn -> Landing</c>: after the S-turns the aircraft entered
/// <see cref="LandingPhase"/> still far out and descended at the category rate with no
/// glideslope tracking, touching down short of the runway — the same failure mode as a 360
/// on final (<c>L360</c>/<c>R360</c>, fixed in ClonePatternPhase).
///
/// The fix re-inserts a <see cref="FinalApproachPhase"/> resume after the S-turns so the
/// aircraft re-captures the glideslope before landing.
/// </summary>
public class MlsOnFinalResumesApproachTests(ITestOutputHelper output)
{
    private const string Callsign = "N172SP";

    [Fact]
    public void Mls_OnFinal_ResumesFinalApproach_BeforeLanding()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "28L");
        if (rwy is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());

        // Place the aircraft ~2 nm out, aligned and on a ~3° glidepath to 28L.
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, 2.0);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = new LatLon(acLat, acLon),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 650,
            IndicatedAirspeed = 75,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                CruiseAltitude = 3000,
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = rwy };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.CurrentPhase);

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-mls-final",
            ScenarioName = "OAK MLS-on-final Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        var result = engine.SendCommand(Callsign, "MLS");
        Assert.True(result.Success, $"MLS failed: {result.Message}");

        var phases = aircraft.Phases.Phases.ToList();
        output.WriteLine($"chain after MLS: [{string.Join(" -> ", phases.Select(p => p.Name))}]");

        int sturnIdx = phases.FindIndex(p => p is STurnPhase);
        Assert.True(sturnIdx >= 0, "MLS did not insert an S-turn phase");

        int landingIdx = phases.FindIndex(sturnIdx + 1, p => p is LandingPhase);
        Assert.True(landingIdx > sturnIdx, "no Landing phase scheduled after the S-turn");

        int resumeIdx = phases.FindIndex(sturnIdx + 1, p => p is FinalApproachPhase);
        Assert.True(
            (resumeIdx > sturnIdx) && (resumeIdx < landingIdx),
            "After MLS on final the chain must resume FinalApproach before landing, but it is "
                + $"[{string.Join(" -> ", phases.Select(p => p.Name))}]. Without the resume the aircraft "
                + "skips to Landing far out and touches down short of the runway."
        );
    }
}

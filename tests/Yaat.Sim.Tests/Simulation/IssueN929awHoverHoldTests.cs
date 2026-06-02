using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Bug: a fixed-wing aircraft (N929AW, a BE33 Bonanza, VFR) was given HPP ("Hold Present
/// Position") and hovered like a helicopter — it decelerated from 134 kt to 0 IAS and sat
/// motionless over fix VPCBT. Fixed-wing aircraft cannot hover; the hover-hold commands
/// (HPP / HFIX, both with no turn direction) must be rejected for non-rotorcraft, with
/// guidance toward the directional forms (HPPL/HPPR, HFIXL/HFIXR). Helicopters still hover.
///
/// Recording: S2-OAK-5 | Advanced Concepts — N929AW spawns ~t=1072, is sent DCTF VPCBT,
/// then HPP at t≈1247.
/// </summary>
public class IssueN929awHoverHoldTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n929aw-hover-hold-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N929AW";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Headline E2E regression: replay to just before the recorded HPP, issue HPP on the
    /// airborne fixed-wing, and assert it is rejected and never decelerates to a hover.
    /// Before the fix HPP succeeded and IAS collapsed to 0 within ~65 s.
    /// </summary>
    [Fact]
    public void Hpp_OnFixedWing_IsRejected_AndDoesNotHover()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1245);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.False(aircraft.IsOnGround);
        Assert.True(aircraft.IndicatedAirspeed > 100, $"precondition: cruising, was {aircraft.IndicatedAirspeed:F1} kt");

        var result = engine.SendCommand(Callsign, "HPP");
        Assert.False(result.Success, $"HPP must be rejected for a fixed-wing aircraft (msg: {result.Message})");

        double minIas = aircraft.IndicatedAirspeed;
        for (int t = 1; t <= 90; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            minIas = Math.Min(minIas, aircraft.IndicatedAirspeed);
            Assert.IsNotType<VfrHoldPhase>(aircraft.Phases?.CurrentPhase);
        }

        output.WriteLine($"min IAS after rejected HPP = {minIas:F1} kt");
        Assert.True(minIas > 60, $"fixed-wing must keep flying, not hover; min IAS was {minIas:F1} kt");
        Assert.DoesNotContain(aircraft!.Phases?.Phases ?? [], p => p is VfrHoldPhase);
    }

    private static AircraftState MakeAirborneVfr(string callsign, string aircraftType)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Position = new LatLon(37.72, -122.11),
            TrueHeading = new TrueHeading(115),
            Altitude = 2000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", CruiseAltitude = 3500 },
        };
    }

    private static CommandResult Dispatch(AircraftState aircraft, ParsedCommand command)
    {
        TestVnasData.EnsureInitialized();
        return CommandDispatcher.Dispatch(command, aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));
    }

    [Fact]
    public void Hpp_FixedWingVfr_RejectedWithGuidance()
    {
        var ac = MakeAirborneVfr("N929AW", "BE33");

        var result = Dispatch(ac, new HoldPresentPositionHoverCommand());

        output.WriteLine($"HPP/BE33: Success={result.Success} Message={result.Message}");
        Assert.False(result.Success);
        Assert.Contains("helicopter", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ac.Phases?.Phases ?? [], p => p is VfrHoldPhase);
    }

    [Fact]
    public void Hpp_HelicopterVfr_StillHovers()
    {
        var ac = MakeAirborneVfr("N911HP", "EC35");

        var result = Dispatch(ac, new HoldPresentPositionHoverCommand());

        output.WriteLine($"HPP/EC35: Success={result.Success} Message={result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Contains(ac.Phases!.Phases, p => p is VfrHoldPhase);
        Assert.Equal(0, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void Hfix_FixedWingVfr_RejectedWithGuidance()
    {
        var ac = MakeAirborneVfr("N929AW", "BE33");

        var result = Dispatch(ac, new HoldAtFixHoverCommand("VPCBT", 37.80, -122.09));

        output.WriteLine($"HFIX/BE33: Success={result.Success} Message={result.Message}");
        Assert.False(result.Success);
        Assert.Contains("helicopter", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hfix_HelicopterVfr_StillHolds()
    {
        var ac = MakeAirborneVfr("N911HP", "EC35");

        var result = Dispatch(ac, new HoldAtFixHoverCommand("VPCBT", 37.80, -122.09));

        output.WriteLine($"HFIX/EC35: Success={result.Success} Message={result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Contains(ac.Phases!.Phases, p => p is VfrHoldPhase);
    }
}

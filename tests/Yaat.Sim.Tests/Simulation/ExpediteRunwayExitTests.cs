using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Expedited runway exit (EXP). Two halves:
///
/// 1. Parser: the EXP modifier on EL/ER/EXIT (combinable with NODEL, any order),
///    and that the latent "ER W5 NODEL" taxiway-parsing bug is fixed.
/// 2. E2E: feature request from S2-OAK-5 (seb bundle). QXE6184 lands OAK 28R
///    (Landing active t=790, recorded ER W5 at t=827, vacates ~t=850). With EXP,
///    the pilot clears the runway sooner — takes the earliest reachable exit and
///    brakes harder (max-effort 7.5 kts/s vs firm 5.0) to make it.
/// </summary>
public class ExpediteRunwayExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/expedite-runway-exit-recording.yaat-bug-report-bundle.zip";

    // -------------------- Parser --------------------

    [Fact]
    public void ParseExitRight_Expedite()
    {
        var cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER EXP").Value);
        Assert.True(cmd.Expedite);
        Assert.False(cmd.NoDelete);
        Assert.Null(cmd.Taxiway);
    }

    [Fact]
    public void ParseExitRight_TaxiwayThenExpedite()
    {
        var cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5 EXP").Value);
        Assert.Equal("W5", cmd.Taxiway);
        Assert.True(cmd.Expedite);
        Assert.False(cmd.NoDelete);
    }

    [Fact]
    public void ParseExitLeft_Expedite()
    {
        var cmd = Assert.IsType<ExitLeftCommand>(CommandParser.Parse("EL EXP").Value);
        Assert.True(cmd.Expedite);
        Assert.Null(cmd.Taxiway);
    }

    [Fact]
    public void ParseExitTaxiway_Expedite()
    {
        var cmd = Assert.IsType<ExitTaxiwayCommand>(CommandParser.Parse("EXIT A3 EXP").Value);
        Assert.Equal("A3", cmd.Taxiway);
        Assert.True(cmd.Expedite);
    }

    [Fact]
    public void ParseExitRight_TaxiwayNoDelExp_AnyOrder()
    {
        // NODEL + EXP combine in any order.
        var a = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5 NODEL EXP").Value);
        Assert.Equal("W5", a.Taxiway);
        Assert.True(a.NoDelete);
        Assert.True(a.Expedite);

        var b = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER EXP W5 NODEL").Value);
        Assert.Equal("W5", b.Taxiway);
        Assert.True(b.NoDelete);
        Assert.True(b.Expedite);
    }

    [Fact]
    public void ParseExitRight_TaxiwayNoDel_FixesLatentBug()
    {
        // Before the parser refactor, "ER W5 NODEL" parsed the taxiway as "W5 NODEL".
        var cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5 NODEL").Value);
        Assert.Equal("W5", cmd.Taxiway);
        Assert.True(cmd.NoDelete);
        Assert.False(cmd.Expedite);
    }

    [Fact]
    public void ParseExitRight_PlainTaxiway_StillWorks()
    {
        var cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5").Value);
        Assert.Equal("W5", cmd.Taxiway);
        Assert.False(cmd.NoDelete);
        Assert.False(cmd.Expedite);
    }

    // -------------------- E2E --------------------

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

    private sealed record RolloutResult(string? ExitTaxiway, int VacateSecond, double VacateAlongTrackNm, double MaxDecelKtsPerSec);

    /// <summary>
    /// Replays QXE6184 to the rollout, optionally issues a command (e.g. "EXP"),
    /// then ticks physics only (no recorded actions — so the recorded ER W5 at
    /// t=827 never fires and both runs use default exit selection). Measures the
    /// along-runway distance and sim-second at which the aircraft vacates, plus
    /// the peak deceleration observed during rollout.
    /// </summary>
    private RolloutResult? RunRollout(SimulationEngine engine, SessionRecording recording, string? command)
    {
        const int Start = 800;
        engine.Replay(recording, Start);

        var ac = engine.FindAircraft("QXE6184");
        if (ac is null)
        {
            return null;
        }

        if (command is not null)
        {
            var result = engine.SendCommand("QXE6184", command);
            Assert.True(result.Success, $"SendCommand('{command}') failed: {result.Message}");
        }

        var startPos = ac.Position;
        var runwayHeading = ac.TrueHeading;
        double prevGs = ac.GroundSpeed;
        double maxDecel = 0;

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("QXE6184");
            if (ac is null)
            {
                break;
            }

            double decel = prevGs - ac.GroundSpeed;
            if (decel > maxDecel)
            {
                maxDecel = decel;
            }
            prevGs = ac.GroundSpeed;

            if (ac.Ground.CurrentTaxiway is not null)
            {
                double alongTrack = GeoMath.AlongTrackDistanceNm(ac.Position, startPos, runwayHeading);
                return new RolloutResult(ac.Ground.CurrentTaxiway, t, alongTrack, maxDecel);
            }
        }

        return new RolloutResult(ac?.Ground.CurrentTaxiway, -1, double.NaN, maxDecel);
    }

    /// <summary>
    /// Standalone EXP on a just-landed aircraft: takes an earlier exit (W4 vs the
    /// default W5), clears the runway sooner and at a shorter along-runway
    /// distance, and brakes harder (peak decel above the firm 5.0 kts/s).
    /// </summary>
    [Fact]
    public void StandaloneExp_ClearsRunwaySoonerThanDefault()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var baseline = RunRollout(engine, recording, command: null);
        var expedited = RunRollout(BuildEngine()!, recording, command: "EXP");
        Assert.NotNull(baseline);
        Assert.NotNull(expedited);

        output.WriteLine($"baseline : {baseline}");
        output.WriteLine($"expedited: {expedited}");

        Assert.True(baseline.VacateSecond > 0, "baseline never vacated");
        Assert.True(expedited.VacateSecond > 0, "expedited never vacated");

        // Earlier exit ⇒ shorter runway occupancy (sooner and shorter distance).
        Assert.True(
            expedited.VacateSecond < baseline.VacateSecond,
            $"expedited vacated at t+{expedited.VacateSecond}, baseline at t+{baseline.VacateSecond}"
        );
        Assert.True(
            expedited.VacateAlongTrackNm < baseline.VacateAlongTrackNm,
            $"expedited vacated at {expedited.VacateAlongTrackNm:F3} nm, baseline at {baseline.VacateAlongTrackNm:F3} nm"
        );

        // Brakes harder than the firm 5.0 kts/s used for normal explicit exits.
        Assert.True(expedited.MaxDecelKtsPerSec > 5.0, $"expedited peak decel {expedited.MaxDecelKtsPerSec:F2} should exceed firm 5.0 kts/s");
    }

    /// <summary>
    /// The ER EXP modifier form (explicit side + expedite) also takes an earlier
    /// exit than plain ER, which already brakes firmly. Exercises the
    /// preference-change re-resolution path rather than the standalone-EXP path.
    /// </summary>
    [Fact]
    public void ErExp_TakesEarlierExitThanPlainEr()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var plain = RunRollout(engine, recording, command: "ER");
        var expedited = RunRollout(BuildEngine()!, recording, command: "ER EXP");
        Assert.NotNull(plain);
        Assert.NotNull(expedited);

        output.WriteLine($"ER     : {plain}");
        output.WriteLine($"ER EXP : {expedited}");

        Assert.True(plain.VacateSecond > 0, "ER never vacated");
        Assert.True(expedited.VacateSecond > 0, "ER EXP never vacated");
        Assert.True(
            expedited.VacateAlongTrackNm < plain.VacateAlongTrackNm,
            $"ER EXP vacated at {expedited.VacateAlongTrackNm:F3} nm, ER at {plain.VacateAlongTrackNm:F3} nm"
        );
        Assert.True(expedited.MaxDecelKtsPerSec > 5.0, $"ER EXP peak decel {expedited.MaxDecelKtsPerSec:F2} should exceed firm 5.0 kts/s");
    }
}

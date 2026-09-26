using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Expedited runway exit (EXP). Two halves:
///
/// 1. Parser: the EXP modifier on EL/ER/EXIT (combinable with NODEL, any order),
///    and that the latent "ER W5 NODEL" taxiway-parsing bug is fixed.
/// 2. E2E: feature request from S2-OAK-5 (seb bundle). With EXP the pilot clears
///    the runway sooner — takes the earliest reachable exit and brakes harder
///    (max-effort 7.5 kts/s vs firm 5.0) to make it. Measured on an A320 landing
///    SFO 19L, whose default choice is the high-speed H: the standard exits before
///    it are out of reach at the routine and firm rates but not at max effort.
/// </summary>
public class ExpediteRunwayExitTests(ITestOutputHelper output)
{
    // -------------------- Parser --------------------

    [Fact]
    public void ParseExitRight_Expedite()
    {
        ExitRightCommand cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER EXP").Value);
        Assert.True(cmd.Expedite);
        Assert.False(cmd.NoDelete);
        Assert.Null(cmd.Taxiway);
    }

    [Fact]
    public void ParseExitRight_TaxiwayThenExpedite()
    {
        ExitRightCommand cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5 EXP").Value);
        Assert.Equal("W5", cmd.Taxiway);
        Assert.True(cmd.Expedite);
        Assert.False(cmd.NoDelete);
    }

    [Fact]
    public void ParseExitLeft_Expedite()
    {
        ExitLeftCommand cmd = Assert.IsType<ExitLeftCommand>(CommandParser.Parse("EL EXP").Value);
        Assert.True(cmd.Expedite);
        Assert.Null(cmd.Taxiway);
    }

    [Fact]
    public void ParseExitTaxiway_Expedite()
    {
        ExitTaxiwayCommand cmd = Assert.IsType<ExitTaxiwayCommand>(CommandParser.Parse("EXIT A3 EXP").Value);
        Assert.Equal("A3", cmd.Taxiway);
        Assert.True(cmd.Expedite);
    }

    [Fact]
    public void ParseExitRight_TaxiwayNoDelExp_AnyOrder()
    {
        // NODEL + EXP combine in any order.
        ExitRightCommand a = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5 NODEL EXP").Value);
        Assert.Equal("W5", a.Taxiway);
        Assert.True(a.NoDelete);
        Assert.True(a.Expedite);

        ExitRightCommand b = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER EXP W5 NODEL").Value);
        Assert.Equal("W5", b.Taxiway);
        Assert.True(b.NoDelete);
        Assert.True(b.Expedite);
    }

    [Fact]
    public void ParseExitRight_TaxiwayNoDel_FixesLatentBug()
    {
        // Before the parser refactor, "ER W5 NODEL" parsed the taxiway as "W5 NODEL".
        ExitRightCommand cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5 NODEL").Value);
        Assert.Equal("W5", cmd.Taxiway);
        Assert.True(cmd.NoDelete);
        Assert.False(cmd.Expedite);
    }

    [Fact]
    public void ParseExitRight_PlainTaxiway_StillWorks()
    {
        ExitRightCommand cmd = Assert.IsType<ExitRightCommand>(CommandParser.Parse("ER W5").Value);
        Assert.Equal("W5", cmd.Taxiway);
        Assert.False(cmd.NoDelete);
        Assert.False(cmd.Expedite);
    }

    // -------------------- E2E --------------------

    private const string Callsign = "TST320";

    private sealed record RolloutResult(
        string? ExitTaxiway,
        int VacateSecond,
        double VacateAlongTrackNm,
        double MaxDecelKtsPerSec,
        string? PeakPhase
    );

    /// <summary>
    /// Spawns an A320 on a 1 nm final to SFO 19L (real navdata and ground layout), clears it to land with no exit
    /// instruction, ticks to three seconds past touchdown, optionally issues <paramref name="command"/> (e.g. "EXP"),
    /// then keeps ticking. Measures the along-runway distance and the second (from that point) at which it vacates,
    /// plus the peak one-second deceleration and the phase that flew it.
    /// </summary>
    private RolloutResult? RunRollout(string? command)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("SFO", "19L", "A320", Callsign);
        if (spawned is null)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState ac, RunwayInfo runway) = spawned;

        for (int t = 0; (t < 60) && !ac.IsOnGround; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(ac.IsOnGround, $"{Callsign} should touch down within 60 s of the spawn");
        double touchdownIas = ac.IndicatedAirspeed;

        // A few seconds into the rollout F1 needs more than the firm 5.0 kt/s but less than the A320's 7.5 kt/s
        // max-effort rate, so only an expedited exit can still make it.
        const int CommandDelaySeconds = 3;
        for (int t = 0; t < CommandDelaySeconds; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(
            ac.IsOnGround && (ac.IndicatedAirspeed < touchdownIas),
            $"{Callsign} should be rolling out {CommandDelaySeconds} s after touchdown: "
                + $"IAS {ac.IndicatedAirspeed:F1} kt, touchdown {touchdownIas:F1} kt"
        );

        if (command is not null)
        {
            CommandResult result = engine.SendCommand(Callsign, command);
            Assert.True(result.Success, $"SendCommand('{command}') failed: {result.Message}");
        }

        LatLon startPos = ac.Position;
        TrueHeading runwayHeading = runway.TrueHeading;
        double prevGs = ac.GroundSpeed;
        double maxDecel = 0;
        string? peakPhase = null;

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();

            double decel = prevGs - ac.GroundSpeed;
            if (decel > maxDecel)
            {
                maxDecel = decel;
                peakPhase = $"{ac.Phases?.CurrentPhase?.Name} t+{t}s gs={ac.GroundSpeed:F1}";
            }
            prevGs = ac.GroundSpeed;

            if (ac.Ground.CurrentTaxiway is not null)
            {
                double alongTrack = GeoMath.AlongTrackDistanceNm(ac.Position, startPos, runwayHeading);
                return new RolloutResult(ac.Ground.CurrentTaxiway, t, alongTrack, maxDecel, peakPhase);
            }
        }

        return new RolloutResult(ac.Ground.CurrentTaxiway, -1, double.NaN, maxDecel, peakPhase);
    }

    /// <summary>
    /// Standalone EXP on a just-landed aircraft: takes an earlier exit (the standard
    /// F1 vs the default high-speed H), clears the runway sooner and at a shorter
    /// along-runway distance, and brakes harder (peak decel above the firm 5.0 kts/s).
    /// </summary>
    [Fact]
    public void StandaloneExp_ClearsRunwaySoonerThanDefault()
    {
        RolloutResult? baseline = RunRollout(command: null);
        RolloutResult? expedited = RunRollout(command: "EXP");
        if ((baseline is null) || (expedited is null))
        {
            return;
        }

        output.WriteLine($"baseline : {baseline}");
        output.WriteLine($"expedited: {expedited}");

        Assert.True(baseline.VacateSecond > 0, "baseline never vacated");
        Assert.True(expedited.VacateSecond > 0, "expedited never vacated");
        Assert.Equal("H", baseline.ExitTaxiway);
        Assert.Equal("F1", expedited.ExitTaxiway);

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
        RolloutResult? plain = RunRollout(command: "ER");
        RolloutResult? expedited = RunRollout(command: "ER EXP");
        if ((plain is null) || (expedited is null))
        {
            return;
        }

        output.WriteLine($"ER     : {plain}");
        output.WriteLine($"ER EXP : {expedited}");

        Assert.True(plain.VacateSecond > 0, "ER never vacated");
        Assert.True(expedited.VacateSecond > 0, "ER EXP never vacated");
        Assert.Equal("H", plain.ExitTaxiway);
        Assert.Equal("F1", expedited.ExitTaxiway);
        Assert.True(
            expedited.VacateAlongTrackNm < plain.VacateAlongTrackNm,
            $"ER EXP vacated at {expedited.VacateAlongTrackNm:F3} nm, ER at {plain.VacateAlongTrackNm:F3} nm"
        );
        Assert.True(expedited.MaxDecelKtsPerSec > 5.0, $"ER EXP peak decel {expedited.MaxDecelKtsPerSec:F2} should exceed firm 5.0 kts/s");
    }
}

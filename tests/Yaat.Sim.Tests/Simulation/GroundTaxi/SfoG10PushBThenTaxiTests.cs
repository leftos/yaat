using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-718: THY9WC (B789, SFO stand G10) runs the S1-SFO-2 Ground Control 28/01 presets <c>PUSH B</c>,
/// <c>WAIT 10 TAXI B B5 $10</c>. The bare push lines up on B's northern branch facing 225, from where <c>TAXI B B5 $10</c>
/// turns about. The push's facing stays the RPO's to give, so the RPO is warned instead, naming the pushes that would
/// have faced the route: on the live TAXI's result line, or as an amber warning when a scripted or deferred TAXI fires.
/// </summary>
public sealed partial class SfoG10PushBThenTaxiTests(ITestOutputHelper output)
{
    private const string Callsign = "THY9WC";
    private const string AircraftType = "B789";
    private const string Stand = "G10";
    private const string Taxi = "TAXI B B5 $10";

    /// <summary>The warning a push onto B facing 225 true (212 magnetic, as the terminal shows it) earns from <see cref="Taxi"/>.</summary>
    private const string PushBWarning = Callsign + " " + PushBAmberWarning;

    /// <summary>
    /// The same warning as an amber line: without the callsign, which the client prefixes to every warning entry
    /// (<c>THY9WC: pushed facing …</c>).
    /// </summary>
    private const string PushBAmberWarning = "pushed facing 212 on B and turns about to taxi; PUSH B B5 or PUSH B FACE NE would have faced the route";

    /// <summary>What every turn-about warning carries, to find it among other text.</summary>
    private const string TurnsAbout = "turns about to taxi";

    /// <summary>Tick budget for a push to end, seconds: a failure timeout, not a cost.</summary>
    private const int BudgetSeconds = 600;

    /// <summary>Seconds ticked after a scripted TAXI starts, for its warning to drain.</summary>
    private const int DrainSeconds = 3;

    /// <summary>The pilot's reaction delay, seconds, for the reaction-delay case: it defers the typed TAXI.</summary>
    private const int ReactionDelaySeconds = 2;

    [Fact]
    public void LivePushB_ThenTaxiBB5_WarnsOnTheResultLine()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        PushAndHold(setup, "PUSH B");
        string message = SendTaxi(setup);

        Assert.EndsWith($" ({PushBWarning})", message);
    }

    [Fact]
    public void PresetPushB_ThenWaitTaxiBB5_RaisesOneAmberWarningWhenTheTaxiFires()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        List<string> warnings = CollectWarnings(setup);
        DispatchPresets(setup, "PUSH B", $"WAIT 10 {Taxi}");

        AssertOneAmberWarningOnceTaxiing(setup, warnings);
    }

    [Fact]
    public void PresetSingleStringPushBWaitTaxiBB5_RaisesOneAmberWarningWhenTheTaxiFires()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        List<string> warnings = CollectWarnings(setup);
        DispatchPresets(setup, $"PUSH B ; WAIT 10 {Taxi}");

        AssertOneAmberWarningOnceTaxiing(setup, warnings);
    }

    [Fact]
    public void LivePushB_ThenLiveWaitTaxiBB5_RaisesOneAmberWarningAndNoneOnTheResultLine()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        PushAndHold(setup, "PUSH B");
        List<string> warnings = CollectWarnings(setup);
        CommandResult waited = setup.Ground.Engine.SendCommand(Callsign, $"WAIT 10 {Taxi}");
        Assert.True(waited.Success, $"WAIT 10 {Taxi} refused: {waited.Message}");
        output.WriteLine($"WAIT 10 {Taxi}: {waited.Message}");

        Assert.DoesNotContain(TurnsAbout, waited.Message ?? "", StringComparison.Ordinal);
        AssertOneAmberWarningOnceTaxiing(setup, warnings);
    }

    [Fact]
    public void LivePushB_ThenLiveTaxiBB5UnderReactionDelay_RaisesOneAmberWarningAndNoneOnTheResultLine()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        PushAndHold(setup, "PUSH B");
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(setup.Ground.Engine.Scenario);
        scenario.CommandRunDelayMinSeconds = ReactionDelaySeconds;
        scenario.CommandRunDelayMaxSeconds = ReactionDelaySeconds;
        List<string> warnings = CollectWarnings(setup);
        CommandResult taxi = setup.Ground.Engine.SendCommand(Callsign, Taxi);
        Assert.True(taxi.Success, $"{Taxi} refused: {taxi.Message}");
        output.WriteLine($"{Taxi}: {taxi.Message}");
        Assert.NotEmpty(setup.Aircraft.DeferredDispatches);

        Assert.DoesNotContain(TurnsAbout, taxi.Message ?? "", StringComparison.Ordinal);
        AssertOneAmberWarningOnceTaxiing(setup, warnings);
    }

    [Fact]
    public void LiveChainPushBThenTaxiBB5_RaisesOneAmberWarningAndNoneOnTheResultLine()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        List<string> warnings = CollectWarnings(setup);
        string chain = $"PUSH B ; {Taxi}";
        CommandResult chained = setup.Ground.Engine.SendCommand(Callsign, chain);
        Assert.True(chained.Success, $"{chain} refused: {chained.Message}");
        output.WriteLine($"{chain}: {chained.Message}");

        Assert.DoesNotContain(TurnsAbout, chained.Message ?? "", StringComparison.Ordinal);
        AssertOneAmberWarningOnceTaxiing(setup, warnings);
    }

    [Fact]
    public void LivePushBFacingB5_ThenTaxiBB5_DoesNotWarn()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        PushAndHold(setup, "PUSH B B5");
        string message = SendTaxi(setup);

        Assert.DoesNotContain(TurnsAbout, message, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePushBFaceSw_ThenTaxiBB5_StillWarns()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        PushAndHold(setup, "PUSH B FACE SW");
        string message = SendTaxi(setup);

        // This push ends 113 ft off B's nearest edge, so the warning names no lane.
        Assert.Matches(@"\(THY9WC pushed facing \d{3} and turns about to taxi; PUSH FACE NE would have faced the route\)$", message);
    }

    [Fact]
    public void LiveBarePush_ThenTaxiThatTurnsAbout_NamesOnlyPushFace()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        PushAndHold(setup, "PUSH");
        Assert.Null(setup.Aircraft.Ground.CurrentTaxiway);
        string message = SendTaxi(setup);

        Assert.Matches(BarePushWarning(), message);
    }

    [Fact]
    public void LivePushBFaceNe_ThenTaxiBB5_DoesNotWarn()
    {
        if (Setup() is not { } setup)
        {
            return;
        }

        PushAndHold(setup, "PUSH B FACE NE");
        string message = SendTaxi(setup);

        Assert.DoesNotContain(TurnsAbout, message, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\(THY9WC pushed facing \d{3} and turns about to taxi; PUSH FACE (N|NE|E|SE|S|SW|W|NW) would have faced the route\)$")]
    private static partial Regex BarePushWarning();

    private (SfoGround Ground, AircraftState Aircraft)? Setup()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return null;
        }

        return (ground, SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, Stand));
    }

    /// <summary>Every amber warning the engine raises for <see cref="Callsign"/> from now on.</summary>
    private static List<string> CollectWarnings((SfoGround Ground, AircraftState Aircraft) setup)
    {
        List<string> warnings = [];
        setup.Ground.Engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == Callsign)
            {
                warnings.Add(warning);
            }
        };
        return warnings;
    }

    /// <summary>Dispatches <paramref name="presets"/> as the aircraft's scenario presets.</summary>
    private static void DispatchPresets((SfoGround Ground, AircraftState Aircraft) setup, params string[] presets) =>
        setup.Ground.Engine.DispatchPresetCommands(
            new LoadedAircraft { State = setup.Aircraft, PresetCommands = [.. presets.Select(command => new PresetCommand { Command = command })] }
        );

    /// <summary>Ticks until the TAXI starts and its warning drains, then asserts exactly one turn-about warning, the push onto B's.</summary>
    private void AssertOneAmberWarningOnceTaxiing((SfoGround Ground, AircraftState Aircraft) setup, List<string> warnings)
    {
        int taxiStart = SfoGroundHarness.TickUntil(
            setup.Ground.Engine,
            () => setup.Aircraft.Phases?.CurrentPhase is TaxiingPhase,
            BudgetSeconds,
            null
        );
        Assert.True(taxiStart > 0, $"the TAXI never started; phase {setup.Aircraft.Phases?.CurrentPhase?.Name ?? "(none)"}");
        SfoGroundHarness.TickUntil(setup.Ground.Engine, () => false, DrainSeconds, null);
        output.WriteLine($"warnings: {string.Join(" | ", warnings)}");

        Assert.Equal(PushBAmberWarning, Assert.Single(warnings, w => w.Contains(TurnsAbout, StringComparison.Ordinal)));
    }

    /// <summary>Issues <paramref name="push"/> live and ticks until the aircraft holds after it.</summary>
    private void PushAndHold((SfoGround Ground, AircraftState Aircraft) setup, string push)
    {
        CommandResult pushed = setup.Ground.Engine.SendCommand(Callsign, push);
        Assert.True(pushed.Success, $"{push} refused: {pushed.Message}");
        AircraftState ac = setup.Aircraft;
        int held = SfoGroundHarness.TickUntil(setup.Ground.Engine, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase, BudgetSeconds, null);
        Assert.True(held > 0, $"{push} never ended holding after the push; phase {ac.Phases?.CurrentPhase?.Name ?? "(none)"}");
        output.WriteLine(
            $"{push}: held t={held}s at ({ac.Position.Lat:F6}, {ac.Position.Lon:F6}) true {ac.TrueHeading.Degrees:F1} "
                + $"magnetic {ac.MagneticHeading.Degrees:F1} on {ac.Ground.CurrentTaxiway ?? "(no taxiway)"}"
        );
    }

    /// <summary>Issues <see cref="Taxi"/> live and returns its accepted result line.</summary>
    private string SendTaxi((SfoGround Ground, AircraftState Aircraft) setup)
    {
        CommandResult taxi = setup.Ground.Engine.SendCommand(Callsign, Taxi);
        Assert.True(taxi.Success, $"{Taxi} refused: {taxi.Message}");
        output.WriteLine($"{Taxi}: {taxi.Message}");
        foreach (TaxiRouteSegment segment in setup.Aircraft.Ground.AssignedTaxiRoute?.Segments.Take(4) ?? [])
        {
            double bearing = GeoMath.BearingTo(segment.Edge.FromNode.Position, segment.Edge.ToNode.Position);
            output.WriteLine($"  {segment.TaxiwayName} {segment.FromNodeId}->{segment.ToNodeId} true {bearing:F1}");
        }

        return taxi.Message ?? "";
    }
}

using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A <c>TAXI D HS J</c> from the OAK ramp with no destination ends the route with a RouteIncomplete hold at J, backed
/// by the explicit J bar 116 ft earlier: the navigator plans nothing past a target it must stop at, so the cut's own
/// set-back stop cannot reach back before the explicit bar and brake the aircraft short of it.
///
/// Recording: S1-OAK-P (A) | S1 Rating Practical Exam (shared with <see cref="OakCtoAtTaxiwayHoldShortTests"/>).
/// </summary>
[Collection("NavDbMutator")]
public class OakTaxiNoDestinationHoldShortTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-hs-directional-hint-recording.zip";
    private const string Callsign = "N45912";
    private const int JustBeforeTaxi = 1363;

    /// <summary>The arrival that exits 28R onto E while N45912 taxis out, and stops on E.</summary>
    private const string ExitingTrafficOnE = "N314WN";

    /// <summary>Hang guard for the taxi to the hold short of J, in simulated seconds.</summary>
    private const int TaxiToHoldLimitSeconds = 900;

    /// <summary>The stop the working <c>TAXI D C HS J RWY 28R</c> route reaches at the same J bar.</summary>
    private const double ExpectedStopLat = 37.731151;

    private const double ExpectedStopLon = -122.216029;

    /// <summary>How far (ft) from that stop the no-destination route may come to rest at the same bar.</summary>
    private const double StopToleranceFt = 15.0;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("a no-destination taxi that must hold short of a taxiway");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// <c>TAXI D HS J</c> and <c>TAXI D J HS J</c> from the ramp with no destination: the aircraft taxis D and holds
    /// short of J, at the stop the working D C route reaches at the same bar.
    /// </summary>
    [Theory]
    [InlineData("TAXI D HS J")]
    [InlineData("TAXI D J HS J")]
    public void DHsJ_NoDestination_HoldsShortOfJ(string clearance)
    {
        SimulationEngine engine = EngineTaxiing(clearance);

        AircraftState held = TickUntil(engine, a => IsHoldingShortOf(a, "J"), TaxiToHoldLimitSeconds, "holding short of J");

        double offFt = GeoMath.DistanceNm(held.Position, new LatLon(ExpectedStopLat, ExpectedStopLon)) * GeoMath.FeetPerNm;
        Assert.True(
            offFt <= StopToleranceFt,
            $"{Callsign} holds short of J {offFt:F1} ft from the stop the D C route reaches at the same bar: "
                + $"pos=({held.Position.Lat:F6}, {held.Position.Lon:F6})"
        );
    }

    /// <summary>
    /// A CROSS queued behind <c>TAXI D J HS J</c> is not applied when the clearance is issued, and fires once the aircraft
    /// holds short of J.
    /// </summary>
    [Fact]
    public void DJHsJ_QueuedCross_FiresAtTheHold()
    {
        SimulationEngine engine = EngineTaxiing("TAXI D J HS J; CROSS 28R");

        AircraftState taxiing = engine.FindAircraft(Callsign)!;
        Assert.False(QueuedCross(taxiing).IsApplied, "the queued CROSS was applied as the clearance was issued");

        AircraftState held = TickUntil(engine, a => IsHoldingShortOf(a, "J"), TaxiToHoldLimitSeconds, "holding short of J");
        Assert.True(
            IsHoldingShortOf(held, "J"),
            $"{Callsign} is not holding short of J when its CROSS is applied: phase={held.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
        Assert.True(QueuedCross(held).IsApplied, "the queued CROSS never fired once the aircraft held short of J");
    }

    /// <summary>The queued CROSS block, not the TAXI block a parked aircraft's clearance applies at once.</summary>
    private static CommandBlock QueuedCross(AircraftState aircraft)
    {
        CommandBlock? cross = aircraft.Queue.Blocks.SingleOrDefault(b => b.ParsedCommands?.Any(c => c is CrossRunwayCommand) == true);
        Assert.NotNull(cross);
        return cross;
    }

    private AircraftState TickUntil(SimulationEngine engine, Func<AircraftState, bool> reached, int limitSeconds, string what)
    {
        for (int t = 0; t <= limitSeconds; t++)
        {
            AircraftState? aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            if (reached(aircraft))
            {
                output.WriteLine($"{Callsign} {what} after {t}s");
                return aircraft;
            }

            engine.TickOneSecond();
        }

        AircraftState? last = engine.FindAircraft(Callsign);
        Assert.Fail(
            $"hung: {Callsign} never {what} within {limitSeconds}s: phase={last?.Phases?.CurrentPhase?.Name ?? "(none)"} "
                + $"pos=({last?.Position.Lat:F6}, {last?.Position.Lon:F6})"
        );
        return null!;
    }

    private static bool IsHoldingShortOf(AircraftState aircraft, string taxiway) =>
        aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: { } target }
        && string.Equals(target, taxiway, StringComparison.OrdinalIgnoreCase);

    /// <summary>Replays to the taxi, deletes N314WN and clears N45912 with <paramref name="clearance"/>.</summary>
    private SimulationEngine EngineTaxiing(string clearance)
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(recording);
        Assert.NotNull(engine);
        engine.Replay(recording, JustBeforeTaxi);
        Assert.True(engine.SendCommand(ExitingTrafficOnE, "DEL").Success);
        CommandResult taxi = engine.SendCommand(Callsign, clearance);
        Assert.True(taxi.Success, $"{clearance} refused: {taxi.Message}");
        return engine;
    }
}

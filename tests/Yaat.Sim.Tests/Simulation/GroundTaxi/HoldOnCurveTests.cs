using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A HOLD taken while the aircraft is mid-fillet must pin the published speed without skipping the
/// steering tick. <see cref="GroundNavigator"/>'s contract is that during a curve the pose is a pure
/// function of one progress scalar, so the phase has to advance that scalar for every foot physics
/// integrates. Freezing the navigator while physics still brakes the aircraft down the curve leaves the
/// playback pose behind the aircraft; the first tick after the hold lifts writes the aircraft back to it
/// — a backwards jump that <see cref="GroundNavigator.CheckNoTeleport"/> throws on in tests and logs as
/// an error in the shipping app, where it is the visible "aircraft jumps backwards after RES".
/// </summary>
public class HoldOnCurveTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/a67670e50d58.zip";

    /// <summary>OAK ramp-out taxi that runs over the "F - RAMP" fillet at ~8 kt a little after t=500.</summary>
    private const string Callsign = "N784ME";

    /// <summary>Replay start: well before the aircraft reaches the fillet.</summary>
    private const int SearchFromSeconds = 200;

    /// <summary>Sub-tick budget for finding the aircraft mid-fillet (0.25 s each, so 400 s of replay).</summary>
    private const int SearchSubTicks = 1600;

    /// <summary>Speed (kts) the aircraft must still carry on the curve for the hold to be a braking hold.</summary>
    private const double OnCurveSpeedKts = 4.0;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiingPhase", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void HoldOnFillet_ThenRes_KeepsTheAircraftOnTheCurve()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        engine.Replay(recording, SearchFromSeconds);

        AircraftState? ac = null;
        TaxiingPhase? taxi = null;
        for (int sub = 0; sub < SearchSubTicks; sub++)
        {
            engine.ReplayOneSubTick();
            ac = engine.FindAircraft(Callsign);
            if (ac?.Phases?.CurrentPhase is TaxiingPhase onCurve && onCurve.IsNavigatorOnCurve && (ac.IndicatedAirspeed >= OnCurveSpeedKts))
            {
                taxi = onCurve;
                break;
            }
        }

        Assert.True(taxi is not null, $"{Callsign} never taxied a curve at >= {OnCurveSpeedKts:F0} kt within {SearchSubTicks} sub-ticks");
        Assert.NotNull(ac);

        TaxiRoute? route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        int segmentAtHold = route.CurrentSegmentIndex;
        output.WriteLine(
            $"on curve: taxiway={ac.Ground.CurrentTaxiway} seg={segmentAtHold}/{route.Segments.Count} "
                + $"ias={ac.IndicatedAirspeed:F1}kt pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})"
        );

        CommandResult hold = engine.SendCommand(Callsign, "HOLD");
        Assert.True(hold.Success, $"HOLD rejected: {hold.Message}");

        for (int t = 1; t <= 4; t++)
        {
            engine.TickOneSecond();
        }

        AircraftState? held = engine.FindAircraft(Callsign);
        Assert.NotNull(held);
        Assert.IsType<TaxiingPhase>(held.Phases?.CurrentPhase);
        Assert.True(held.IndicatedAirspeed < 1.0, $"{Callsign} should be stopped by HOLD but IAS={held.IndicatedAirspeed:F1}kt");

        LatLon releasePos = held.Position;
        TrueHeading releaseHdg = held.TrueHeading;
        output.WriteLine($"held: pos=({releasePos.Lat:F6},{releasePos.Lon:F6}) hdg={releaseHdg.Degrees:F1}");

        CommandResult res = engine.SendCommand(Callsign, "RES");
        Assert.True(res.Success, $"RES rejected: {res.Message}");

        // The tick that used to write the frozen playback pose: the aircraft must move forward along the
        // curve it was on, never back to where the playback still thought it was.
        engine.TickOneSecond();
        AircraftState? resumed = engine.FindAircraft(Callsign);
        Assert.NotNull(resumed);
        double forwardFt = GeoMath.AlongTrackDistanceNm(resumed.Position, releasePos, releaseHdg) * GeoMath.FeetPerNm;
        output.WriteLine($"first tick after RES: forward={forwardFt:F1}ft ias={resumed.IndicatedAirspeed:F1}kt");
        Assert.True(forwardFt > -1.0, $"{Callsign} was written {-forwardFt:F1}ft backwards on the tick after RES");

        // ...and it goes on to reach the node the curve ends at.
        bool advanced = false;
        for (int t = 1; t <= 30 && !advanced; t++)
        {
            engine.TickOneSecond();
            AircraftState? moving = engine.FindAircraft(Callsign);
            advanced = (moving?.Ground.AssignedTaxiRoute?.CurrentSegmentIndex ?? segmentAtHold) > segmentAtHold;
        }
        Assert.True(advanced, $"{Callsign} never reached the node past the curve after RES (still on segment {segmentAtHold})");
    }
}

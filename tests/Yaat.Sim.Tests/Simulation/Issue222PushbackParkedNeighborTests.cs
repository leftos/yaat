using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #222: pushback stalls when an aircraft is parked at the
/// adjacent gate. In the OAK S1 practical-exam scenario, SWA1182 (B737) at gate 25
/// issues <c>PUSH TE</c> at t=340, but <see cref="GroundConflictDetector"/> hard-pins
/// it to SpeedLimit=0 because SWA3998 (B737) is parked at gate 26 (~145 ft away, inside
/// the 200 ft pushback buffer and in the rear ±90° arc). The user had to issue BREAK
/// three times (t=411, 461, 484) to walk it out. After the fix a genuinely parked/held
/// neighbor is a passable obstacle, so the pushback completes on its own.
///
/// Recording: <c>issue222-oak-pushback-parked-neighbor-recording.zip</c> (S1-OAK-P, ZOA),
/// trimmed to ~t=520. Assertions are scoped to before the user's first BREAK (t=411) so
/// that BREAK cannot mask the bug.
/// </summary>
public class Issue222PushbackParkedNeighborTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue222-oak-pushback-parked-neighbor-recording.zip";

    private const string Pusher = "SWA1182";
    private const string ParkedNeighbor = "SWA3998";

    // PUSH TE is at t=340; the first (masking) BREAK is at t=411.
    private const int AfterPush = 341;
    private const int FirstBreak = 411;

    /// <summary>How long the replay-free push may take; a straight push onto TE takes well under this at 5 kt.</summary>
    private const int ReplayFreeBudgetSeconds = 120;

    /// <summary>The longest run of seconds the pusher may sit at a zero speed limit; a judgement call for "a few seconds".</summary>
    private const int MaxHoldSeconds = 5;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    /// <summary>A B737 parked on the named OAK stand, nose on the stand heading, as the recording spawns it.</summary>
    private static AircraftState SpawnParked(SimulationEngine engine, AirportGroundLayout layout, string callsign, string parkingName)
    {
        GroundNode stand =
            layout.FindParkingByName(parkingName) ?? throw new InvalidOperationException($"OAK layout has no parking named '{parkingName}'");
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B737",
            Position = stand.Position,
            TrueHeading = stand.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// The separation and the lateral room the detector measures against the neighbour — the separation times the
    /// sine of the angle between the push direction and the bearing to it — beside the room a pass needs: the two
    /// half-spans plus <see cref="GroundConflictDetector.WingtipBufferFt"/>.
    /// </summary>
    private static string DescribeLateral(AircraftState pusher, AircraftState neighbour)
    {
        double sepFt = GeoMath.DistanceNm(pusher.Position, neighbour.Position) * GeoMath.FeetPerNm;
        double bearingDeg = GeoMath.BearingTo(pusher.Position, neighbour.Position);
        double pushDeg = pusher.Ground.PushbackTrueHeading?.Degrees ?? pusher.TrueHeading.Degrees;
        double offPushDeg = new TrueHeading(pushDeg).AbsAngleTo(new TrueHeading(bearingDeg));
        double lateralFt = sepFt * Math.Sin(offPushDeg * Math.PI / 180.0);
        double requiredFt =
            (FaaAircraftDatabase.Get(pusher.AircraftType)?.WingspanFt ?? double.NaN) / 2.0
            + (FaaAircraftDatabase.Get(neighbour.AircraftType)?.WingspanFt ?? double.NaN) / 2.0
            + GroundConflictDetector.WingtipBufferFt;
        return $"gs={pusher.GroundSpeed:F1}kt sep={sepFt:F0}ft push={pushDeg:F1}° bearing={bearingDeg:F1}° ({offPushDeg:F1}° off) "
            + $"lateral={lateralFt:F1}ft required={requiredFt:F1}ft";
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay-free: <see cref="Pusher"/> (a B737) parked on OAK gate 25 and <see cref="ParkedNeighbor"/> (a B737)
    /// parked on gate 26, as the recording spawns them. <c>PUSH TE</c> completes on its own, and the detector never
    /// holds the pusher at a zero speed limit for more than <see cref="MaxHoldSeconds"/> in a row.
    /// </summary>
    [Fact]
    public void PushTe_FromGate25_PastParkedNeighbourAtGate26_CompletesWithoutBeingHeld()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-issue222-replay-free",
                ScenarioName = "Issue 222 replay-free",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = false,
            },
        };
        AircraftState pusher = SpawnParked(engine, layout, Pusher, "25");
        AircraftState neighbour = SpawnParked(engine, layout, ParkedNeighbor, "26");

        CommandResult push = engine.SendCommand(Pusher, "PUSH TE");
        output.WriteLine($"PUSH TE: success={push.Success} msg={push.Message}");
        Assert.True(push.Success, $"PUSH TE off gate 25 was refused: {push.Message}");

        int completedAt = -1;
        int holdSeconds = 0;
        int longestHold = 0;
        for (int t = 1; t <= ReplayFreeBudgetSeconds; t++)
        {
            engine.TickOneSecond();
            if (pusher.Phases?.CurrentPhase is HoldingAfterPushbackPhase)
            {
                completedAt = t;
                break;
            }

            bool held = pusher.Ground.SpeedLimit is <= 0.0;
            holdSeconds = held ? holdSeconds + 1 : 0;
            longestHold = Math.Max(longestHold, holdSeconds);
            if (held)
            {
                output.WriteLine($"t={t}s held: yield={pusher.Ground.AutoYieldTarget ?? "-"} {DescribeLateral(pusher, neighbour)}");
            }
        }

        output.WriteLine($"completed at t={completedAt}s, longest hold {longestHold}s");
        Assert.True(
            completedAt > 0,
            $"{Pusher} never completed PUSH TE within {ReplayFreeBudgetSeconds}s (phase={pusher.Phases?.CurrentPhase?.Name})"
        );
        Assert.True(longestHold <= MaxHoldSeconds, $"{Pusher} was held at a zero speed limit for {longestHold}s by {ParkedNeighbor}");
    }

    [Fact]
    public void Pushback_CompletesWithoutBreak_PastParkedNeighbor()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, AfterPush);

        AircraftState? start = engine.FindAircraft(Pusher);
        Assert.NotNull(start);
        Assert.Equal("Pushback", start.Phases?.CurrentPhase?.Name);
        LatLon startPos = start.Position;

        // Tick faithfully but stop before the user's first BREAK (t=411) so it cannot
        // free the aircraft — proving the pushback clears the parked neighbor on its own.
        bool completed = false;
        int completedAt = -1;
        double maxGapClosedFt = 0;
        for (int t = AfterPush; t < FirstBreak; t++)
        {
            AircraftState? ac = engine.FindAircraft(Pusher);
            if (ac is not null)
            {
                maxGapClosedFt = Math.Max(maxGapClosedFt, GeoMath.DistanceNm(ac.Position, startPos) * 6076.12);
                if (ac.Phases?.CurrentPhase?.Name == "Holding After Pushback")
                {
                    completed = true;
                    completedAt = t;
                    break;
                }
            }
            engine.ReplayOneSecond();
        }

        output.WriteLine($"completed={completed} at t={completedAt}, pushed {maxGapClosedFt:F0}ft");

        Assert.True(
            completed,
            $"{Pusher} never completed pushback before the user's first BREAK (t={FirstBreak}): it stayed pinned in Pushback "
                + $"by {ParkedNeighbor} parked at the adjacent gate (pushed only {maxGapClosedFt:F0}ft)."
        );
    }
}

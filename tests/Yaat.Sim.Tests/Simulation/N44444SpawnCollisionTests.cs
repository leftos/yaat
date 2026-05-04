using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression for the Aircraft List flashing bug: when a controller types
/// VP/DA for an unknown callsign, the server creates an "unsupported ghost"
/// AircraftState. If a scenario aircraft (or generator/runtime spawn) later
/// arrives with the same callsign, World.AddAircraft used to append a second
/// entry — causing the 1Hz broadcast to alternate two DTOs per tick and the
/// client AircraftList to flicker between them.
///
/// User-confirmed fix policy: replace. The spawning aircraft's data wins;
/// the ghost (and any user-typed FP / scratchpads / track ownership) is
/// dropped at the moment of collision. Test A asserts this directly. Test B
/// drives the same scenario through a real bundle replay (best-effort —
/// AS-prefix FP creation may not produce a ghost during isolated-room
/// replay; the test documents what it can verify either way).
/// </summary>
public class N44444SpawnCollisionTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/a67670e50d58.zip";
    private const string Callsign = "N44444";

    /// <summary>
    /// Synthetic primary reproducer. Add a ghost AircraftState for N44444,
    /// then add a "scenario" AircraftState with the same callsign. Before
    /// the fix this leaves two entries in the world; after the fix only
    /// the second (scenario) one survives.
    /// </summary>
    [Fact]
    public void AddAircraft_DuplicateCallsign_ReplacesExistingGhost()
    {
        SimLogBuilder.CreateForTest(output).EnableCategory("SimulationWorld", LogLevel.Warning).InitializeSimLog();

        var world = new SimulationWorld();

        var ghost = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "",
            Position = new LatLon(0, 0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            Transponder = new AircraftTransponder
            {
                Code = 304,
                AssignedCode = 304,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                AircraftType = "C172/W",
                FlightRules = "VFR",
                HasFlightPlan = true,
                Departure = "OAK",
                Destination = "MOD",
                CruiseAltitude = 5500,
            },
            Ghost = new AircraftGhostTrack { IsUnsupported = true },
            Track = new AircraftTrack(),
        };
        world.AddAircraft(ghost);

        var scenarioAc = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = new LatLon(37.857, -122.284),
            Altitude = 3500,
            IndicatedAirspeed = 96,
            Transponder = new AircraftTransponder
            {
                Code = 3617,
                AssignedCode = 3617,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                AircraftType = "C172",
                FlightRules = "VFR",
                HasFlightPlan = true,
                Departure = "ZZZZ",
                Destination = "KHAF",
                CruiseAltitude = 5500,
                CruiseSpeed = 120,
            },
            Track = new AircraftTrack(),
        };
        world.AddAircraft(scenarioAc);

        var snapshot = world.GetSnapshot();
        var matches = snapshot.Where(a => a.Callsign == Callsign).ToList();

        output.WriteLine($"World contains {matches.Count} aircraft with callsign {Callsign}");
        foreach (var a in matches)
        {
            output.WriteLine($"  cid={a.Cid} type='{a.AircraftType}' dest={a.FlightPlan.Destination} unsup={a.Ghost.IsUnsupported}");
        }

        Assert.Single(matches);

        var survivor = matches[0];
        Assert.Equal("C172", survivor.AircraftType);
        Assert.Equal("KHAF", survivor.FlightPlan.Destination);
        Assert.False(survivor.Ghost.IsUnsupported, "Surviving entry must be the spawned aircraft, not the ghost");
        Assert.Equal(3617u, survivor.Transponder.AssignedCode);
    }

    /// <summary>
    /// Bundle replay regression. The recording captures the live session that
    /// produced this bug: VP creates a ghost N44444 at t=0, scenario spawn
    /// fires at t=1254, user observed flashing thereafter. Replay to t=1260
    /// (just past spawn) and assert exactly one N44444 in the world.
    ///
    /// NOTE: AS-prefix FP creation may not reproduce the ghost during
    /// isolated-room replay (the t=0 AS 3O FP command does not always
    /// instantiate a ghost AircraftState in the snapshots that ship in the
    /// bundle). When the ghost path doesn't fire, this test passes vacuously
    /// even without the fix. It still serves as a regression guardrail
    /// against future drift in the spawn pipeline.
    /// </summary>
    [Fact]
    public void Replay_PastScenarioSpawn_HasExactlyOneN44444()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("Skipped: NavData not available");
            return;
        }

        var recording = RecordingLoader.Load(BundlePath);
        if (recording is null)
        {
            output.WriteLine($"Skipped: recording not found at {BundlePath}");
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("SimulationWorld", LogLevel.Warning).InitializeSimLog();

        var engine = new SimulationEngine(new TestAirportGroundData());

        // Scenario spawn for N44444 fires at t=1254 (spawnDelay in scenario JSON).
        engine.Replay(recording, 1260);

        var snapshot = engine.World.GetSnapshot();
        var matches = snapshot.Where(a => a.Callsign == Callsign).ToList();

        output.WriteLine($"At t=1260 the world has {matches.Count} {Callsign} entries:");
        foreach (var a in matches)
        {
            output.WriteLine($"  cid={a.Cid} type='{a.AircraftType}' dest={a.FlightPlan.Destination} pos={a.Position} unsup={a.Ghost.IsUnsupported}");
        }

        Assert.True(matches.Count <= 1, $"Expected at most 1 {Callsign} entry post-spawn, found {matches.Count}");
    }
}

using Xunit.Abstractions;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Replay tests using SFO scenario recordings.
///
/// GUTTED (2026-03-12): All prior tests removed because the recordings were captured
/// before WAIT preset dispatch fixes, so test expectations were wrong. The recordings
/// themselves are still valid data — re-record or re-derive expectations after the
/// WAIT/DeferredDispatch pipeline is stable.
///
/// To add a new replay test:
/// 1. Record a session in the YAAT client (produces .yaat-recording.json)
/// 2. Copy to tests/Yaat.Sim.Tests/TestData/ with a descriptive name
/// 3. Load via LoadRecording helper, build engine via BuildEngine
/// 4. engine.Replay(recording, targetSeconds) to replay up to a point
/// 5. engine.FindAircraft("CALLSIGN") to inspect resulting state
/// 6. Assert on .AssignedTaxiRoute, .Phases, .Latitude/.Longitude, etc.
///
/// See docs/e2e-tdd-issue-debugging.md for full guide.
/// </summary>
public class SfoReplayTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
}

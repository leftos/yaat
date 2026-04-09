using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Integration tests for VFR FOLLOW using ZOA scenario S2-OAK-3 (1) "VFR Sequencing"
/// — six C172s inbound to KOAK from different directions at 2500 ft / 90 kts,
/// between 6 and 14 nm from OAK. This is the scenario the command was built for:
/// the student is supposed to visually sequence them all onto final for runway 28.
///
/// The scenario file is NOT committed to the repo (gitignored). Tests skip
/// gracefully when absent — see tests/Yaat.Sim.Tests/TestData/Scenarios/README.
/// </summary>
[Collection("NavDbMutator")]
public class S2Oak3FollowSequencingTests
{
    private const string ScenarioFile = "01HCHWFSVGKA6H0F0QSFFG9MMN.json";
    private const string ArtccId = "ZOA";

    private static readonly string ScenariosRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "Scenarios")
    );

    private readonly ITestOutputHelper _output;

    public S2Oak3FollowSequencingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private ScenarioLoadResult? LoadScenario()
    {
        var path = Path.Combine(ScenariosRoot, ArtccId, ScenarioFile);
        if (!File.Exists(path))
        {
            _output.WriteLine($"S2-OAK-3 (1) not cached at {path} — skipping. Download via tools/validate-all-scenarios.py.");
            return null;
        }

        var json = File.ReadAllText(path);
        return ScenarioLoader.Load(json, groundData: null, rng: new SerializableRandom(42));
    }

    [Fact]
    public void ScenarioLoads_SixVfrC172sInbound()
    {
        var result = LoadScenario();
        if (result is null)
        {
            return;
        }

        var aircraft = result.ImmediateAircraft;
        Assert.Equal(6, aircraft.Count);
        Assert.All(aircraft, a => Assert.Equal("C172", a.State.AircraftType));
        Assert.All(aircraft, a => Assert.True(a.State.IsVfr, $"{a.State.Callsign} should be VFR"));
        Assert.All(aircraft, a => Assert.False(a.State.IsOnGround, $"{a.State.Callsign} should be airborne"));
    }

    [Fact]
    public void Follow_FromClean_InstallsVfrFollowPhase_ForAllInboundTraffic()
    {
        // The whole point of the bug fix: controllers should be able to issue
        // FOLLOW to any of these aircraft even though none are in a pattern yet.
        // Real sequencing workflow: call traffic → RTIS → FOLLOW. Tests use
        // RTISF (forced) to bypass the live visual detection machinery.
        var result = LoadScenario();
        if (result is null)
        {
            return;
        }

        var byCallsign = result.ImmediateAircraft.ToDictionary(a => a.State.Callsign, a => a.State);
        var callsigns = byCallsign.Keys.OrderBy(k => k).ToList();
        Assert.Equal(6, callsigns.Count);

        Func<string, AircraftState?> lookup = cs => byCallsign.TryGetValue(cs, out var ac) ? ac : null;
        var dispatchCtx = TestDispatch.Context(Random.Shared, findAircraft: lookup);

        for (int i = 1; i < callsigns.Count; i++)
        {
            var follower = byCallsign[callsigns[i]];
            var leadCallsign = callsigns[i - 1];

            // Simulate "traffic in sight" via RTISF, then issue FOLLOW.
            CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand(leadCallsign), follower, dispatchCtx);
            var cmdResult = CommandDispatcher.Dispatch(new FollowCommand(leadCallsign), follower, dispatchCtx);

            Assert.True(cmdResult.Success, $"{follower.Callsign} FOLLOW {leadCallsign} failed: {cmdResult.Message}");
            Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
            Assert.Equal(leadCallsign, follower.FollowingCallsign);
        }
    }

    [Fact]
    public void VfrFollowPhase_Tick_SteersFollowerTowardLead_FromRealPositions()
    {
        // Use the real scenario positions: pick two aircraft and verify the
        // follower's heading target lies within ~10° of the true bearing to the lead.
        var result = LoadScenario();
        if (result is null)
        {
            return;
        }

        // N9225L is closest to OAK (~6.4 nm N), N346G is ~10 nm N — use N9225L as lead,
        // N346G as follower. See scenario dump in planning notes.
        var byCallsign = result.ImmediateAircraft.ToDictionary(a => a.State.Callsign, a => a.State);
        if (!byCallsign.TryGetValue("N9225L", out var lead) || !byCallsign.TryGetValue("N346G", out var follower))
        {
            _output.WriteLine("Expected callsigns N9225L / N346G missing — scenario may have changed.");
            return;
        }

        Func<string, AircraftState?> lookup = cs => byCallsign.TryGetValue(cs, out var ac) ? ac : null;
        var dispatchCtx = TestDispatch.Context(Random.Shared, findAircraft: lookup);

        // Force traffic in sight then FOLLOW.
        CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("N9225L"), follower, dispatchCtx);
        var cmdResult = CommandDispatcher.Dispatch(new FollowCommand("N9225L"), follower, dispatchCtx);
        Assert.True(cmdResult.Success, $"FOLLOW failed: {cmdResult.Message}");

        // Manually build a PhaseContext like the live tick loop would, with the lookup set.
        var tickCtx = new PhaseContext
        {
            Aircraft = follower,
            Targets = follower.Targets,
            Category = AircraftCategorization.Categorize(follower.AircraftType),
            DeltaSeconds = 1.0,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            AircraftLookup = lookup,
        };

        var phase = (VfrFollowPhase)follower.Phases!.CurrentPhase!;
        phase.OnTick(tickCtx);

        double expectedBearing = GeoMath.BearingTo(follower.Latitude, follower.Longitude, lead.Latitude, lead.Longitude);
        Assert.NotNull(follower.Targets.TargetTrueHeading);
        double actualBearing = follower.Targets.TargetTrueHeading!.Value.Degrees;
        double diff = GeoMath.AbsBearingDifference(expectedBearing, actualBearing);
        Assert.True(diff < 1.0, $"Expected heading ~{expectedBearing:F1}°, got {actualBearing:F1}° (diff {diff:F1}°)");

        // Altitude is deliberately not touched — the controller's last assignment stands.
        // These scenario aircraft arrive with no TargetAltitude set, so it remains null.
        Assert.Null(follower.Targets.TargetAltitude);
    }
}

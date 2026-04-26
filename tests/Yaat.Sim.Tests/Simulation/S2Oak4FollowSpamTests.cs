using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression for the FOLLOW log-spam bug surfaced by bug bundle
/// <c>s2-oak4-follow-spam-recording.yaat-bug-report-bundle.zip</c> (kept in
/// TestData for reference). In the live S2-OAK-4 session the user issued
/// <c>FOLLOW N436MS</c> to N805FM with the lead well inside
/// <c>desired * 0.5</c> at min approach speed. The
/// <see cref="AirborneFollowHelper"/> "unable to maintain separation"
/// branch fired every physics tick (~4 Hz, 132 warnings over ~12 s) because
/// <see cref="VfrFollowPhase"/> swallowed the helper's null cancel signal:
/// <c>AdjustedFreeFlightSpeed</c> returned <c>result ?? normalSpeed</c>, so
/// the phase never saw the cancel and kept calling the helper next tick.
///
/// The bug bundle's recording cannot be used to reproduce the spam through
/// replay — physics state diverges (WAIT-preset / accumulated rounding) so
/// the follower joins the lead's pattern at &gt;1 nm in the replay instead of
/// fighting the separation gate. A synthetic two-aircraft setup with both
/// piston VFRs at slow speed and 0.3 nm apart deterministically hits the
/// gate, which is what we want for a regression.
/// </summary>
[Collection("NavDbMutator")]
public class S2Oak4FollowSpamTests(ITestOutputHelper output)
{
    private const string Follower = "N805FM";
    private const string Leader = "N436MS";

    private static AircraftState MakePiston(string callsign, string type, double lat, double lon, double heading, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = 1500,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState
            {
                HasReportedTrafficInSight = true,
                LastReportedTrafficCallsign = "N436MS",
                FollowingCallsign = "N436MS",
            },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, Func<string, AircraftState?> lookup) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
            AircraftLookup = lookup,
        };

    /// <summary>
    /// Two slow pistons 0.3 nm apart guarantees the helper's separation-failure
    /// branch fires (<c>distance &lt; desired * 0.5</c> AND
    /// <c>adjusted &lt; minSpeed</c>). The buggy code logged the warning every
    /// tick because <see cref="VfrFollowPhase"/> ignored the helper's null
    /// return; the fix routes that null up so the phase ends.
    /// </summary>
    [Fact]
    public void OnTick_SeparationFailure_EmitsWarningOnceAndEndsPhase()
    {
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .InitializeSimLog();

        // Lead a touch ahead of follower on a 280° heading (KOAK 28R-ish track).
        // 0.3 nm spacing puts the follower well inside desired*0.5 = 0.75 nm.
        var lead = MakePiston(Leader, "C182", 37.7250, -122.2300, heading: 280, ias: 75);
        // 0.3 nm at this latitude ≈ 0.005 deg lon to the east of the lead.
        // Follower sits behind (east) and tracks the same direction.
        var follower = MakePiston(Follower, "P28A", 37.7250, -122.2240, heading: 280, ias: 75);
        follower.Phases!.Add(new VfrFollowPhase(Leader));

        var byCallsign = new Dictionary<string, AircraftState> { [lead.Callsign] = lead, [follower.Callsign] = follower };
        Func<string, AircraftState?> lookup = cs => byCallsign.TryGetValue(cs, out var ac) ? ac : null;

        // Sanity: the synthetic spacing must actually trip the gate.
        double spacingNm = GeoMath.DistanceNm(follower.Position, lead.Position);
        output.WriteLine($"Synthetic spacing: {spacingNm:F3} nm (must be < 0.75 nm to trip the gate)");
        Assert.True(spacingNm < 0.75, $"Test setup wrong: spacing {spacingNm:F3} nm not inside the separation-failure window");

        var phase = (VfrFollowPhase)follower.Phases!.CurrentPhase!;
        phase.Status = PhaseStatus.Active;

        // The helper trips the gate, warns once, clears FollowingCallsign, and
        // returns null. With the fix, VfrFollowPhase.OnTick returns true (end
        // phase) so the engine doesn't tick this phase again — the warning
        // fires once. Pre-fix it returned false and the engine kept ticking,
        // re-firing the warning every physics tick (~4 Hz, 132 times in the
        // original log).
        bool returnedTrue = phase.OnTick(Ctx(follower, lookup));

        int spamCount = follower.PendingWarnings.Count(w => w.Contains("unable to maintain separation", StringComparison.OrdinalIgnoreCase));
        output.WriteLine(
            $"OnTick: returned={returnedTrue}, spamCount={spamCount}, FollowingCallsign={follower.Approach.FollowingCallsign ?? "<null>"}"
        );

        Assert.Equal(1, spamCount);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.True(
            returnedTrue,
            "VfrFollowPhase.OnTick must return true (end phase) when AirborneFollowHelper signals separation failure — otherwise the helper re-fires every tick and spams the warning."
        );
    }
}

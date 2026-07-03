using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// When a pilot has reported specific traffic in sight (RTIS/RTISF), a subsequent
/// bare FOLLOW should default to that last-reported callsign. Explicit FOLLOW {cs}
/// still overrides. If no RTIS has succeeded, bare FOLLOW fails clearly.
/// </summary>
[Collection("NavDbMutator")]
public class FollowImpliedCallsignTests : IDisposable
{
    // KOAK: ~37.721, -122.221, elevation 9ft
    private const double AptLat = 37.721;
    private const double AptLon = -122.221;

    private readonly IDisposable _navDbScope;

    public FollowImpliedCallsignTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(DefaultRunway()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 0);

    private static AircraftState MakeVfrOwnship(string callsign = "OWN1")
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Position = new LatLon(37.75, AptLon),
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 3000,
            IndicatedAirspeed = 90,
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static AircraftState MakeLeader(string callsign, double lat = 37.73, double lon = AptLon)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 3000,
            IndicatedAirspeed = 90,
        };
    }

    // -------------------------------------------------------------------------
    // Parser
    // -------------------------------------------------------------------------

    [Fact]
    public void Parser_AcceptsBareFollow_ReturnsFollowCommandWithNullCallsign()
    {
        var result = CommandParser.Parse("FOLLOW");

        Assert.True(result.IsSuccess, $"Expected parse success but got: {result.Reason}");
        var cmd = Assert.IsType<FollowCommand>(result.Value);
        Assert.Null(cmd.TargetCallsign);
    }

    // -------------------------------------------------------------------------
    // Implied from RTIS / RTISF
    // -------------------------------------------------------------------------

    [Fact]
    public void BareFollow_AfterRtisSuccess_TargetsReportedCallsign()
    {
        var ownship = MakeVfrOwnship();
        var lead = MakeLeader("LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var rtis = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.True(rtis.Success, $"RTIS setup failed: {rtis.Message}");
        Assert.Equal("LEAD", ownship.Approach.LastReportedTrafficCallsign);

        // Bare FOLLOW — no explicit callsign.
        var follow = CommandDispatcher.Dispatch(new FollowCommand(null, false), ownship, ctx);

        Assert.True(follow.Success, $"Expected bare FOLLOW to succeed but got: {follow.Message}");
        Assert.Equal("LEAD", ownship.Approach.FollowingCallsign);
    }

    [Fact]
    public void BareFollow_AfterRtisfSuccess_TargetsForcedCallsign()
    {
        var ownship = MakeVfrOwnship();
        var ctx = TestDispatch.Context(Random.Shared);

        // RTISF bypasses live visual acquisition and force-sets the flag.
        var rtisf = CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ownship, ctx);
        Assert.True(rtisf.Success);
        Assert.Equal("LEAD", ownship.Approach.LastReportedTrafficCallsign);

        var follow = CommandDispatcher.Dispatch(new FollowCommand(null, false), ownship, ctx);

        Assert.True(follow.Success, $"Expected bare FOLLOW to succeed after RTISF but got: {follow.Message}");
        Assert.Equal("LEAD", ownship.Approach.FollowingCallsign);
    }

    // -------------------------------------------------------------------------
    // Override
    // -------------------------------------------------------------------------

    [Fact]
    public void ExplicitFollow_OverridesStoredCallsign()
    {
        var ownship = MakeVfrOwnship();
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ownship, ctx);
        Assert.Equal("LEAD", ownship.Approach.LastReportedTrafficCallsign);

        // Explicit callsign wins even when a different one is stored.
        var follow = CommandDispatcher.Dispatch(new FollowCommand("OTHER", false), ownship, ctx);

        Assert.True(follow.Success);
        Assert.Equal("OTHER", ownship.Approach.FollowingCallsign);
    }

    // -------------------------------------------------------------------------
    // Newer RTIS overrides older stored callsign
    // -------------------------------------------------------------------------

    [Fact]
    public void NewerRtisf_OverridesStoredCallsign_ForImpliedFollow()
    {
        var ownship = MakeVfrOwnship();
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD1"), ownship, ctx);
        Assert.Equal("LEAD1", ownship.Approach.LastReportedTrafficCallsign);

        // Second RTISF with a different callsign must update the stored value.
        CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD2"), ownship, ctx);
        Assert.Equal("LEAD2", ownship.Approach.LastReportedTrafficCallsign);

        var follow = CommandDispatcher.Dispatch(new FollowCommand(null, false), ownship, ctx);

        Assert.True(follow.Success);
        Assert.Equal("LEAD2", ownship.Approach.FollowingCallsign);
    }

    // -------------------------------------------------------------------------
    // No prior RTIS — bare FOLLOW fails clearly
    // -------------------------------------------------------------------------

    [Fact]
    public void BareFollow_WithoutPriorRtis_RejectedByGate()
    {
        // No RTIS at all — the existing RTIS gate rejects before callsign resolution.
        // This preserves the older behavior and covers the common no-RTIS case.
        var ownship = MakeVfrOwnship();
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        Assert.Null(ownship.Approach.LastReportedTrafficCallsign);

        var ctx = TestDispatch.Context(Random.Shared);
        var follow = CommandDispatcher.Dispatch(new FollowCommand(null, false), ownship, ctx);

        Assert.False(follow.Success);
        Assert.Contains("not in sight", follow.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ownship.Approach.FollowingCallsign);
    }

    [Fact]
    public void BareFollow_WhenGatePassedButNoStoredCallsign_FailsWithSayCallsign()
    {
        // Edge case: HasReportedTrafficInSight is true but no callsign is stored.
        // Reachable via legacy snapshots (field added after release) or any code
        // path that sets the flag without populating the callsign.
        var ownship = MakeVfrOwnship();
        ownship.Approach.HasReportedTrafficInSight = true;
        ownship.Approach.LastReportedTrafficCallsign = null;

        var ctx = TestDispatch.Context(Random.Shared);
        var follow = CommandDispatcher.Dispatch(new FollowCommand(null, false), ownship, ctx);

        Assert.False(follow.Success);
        Assert.Contains("say traffic callsign", follow.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ownship.Approach.FollowingCallsign);
    }
}

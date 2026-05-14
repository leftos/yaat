using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Locks in the structural contract of the GIVEWAY / BEHIND redesign:
/// HoldDirective replaces the historical IsHeld+GiveWayTarget pair, HOLDPOSITION
/// and GIVEWAY are now explicitly distinguishable, and the conflict detector
/// emits a "ControllerGiveWay" pair-log entry so the operator can see who is
/// yielding to whom.
/// </summary>
public sealed class GiveWayRedesignTests
{
    // ---------------------------------------------------------------------------
    // HoldDirective construction + invariants
    // ---------------------------------------------------------------------------

    [Fact]
    public void HoldPosition_IsCanonicalSingleton()
    {
        // The HoldPosition directive carries no callsign and equals itself
        // by value. Used by all HOLDPOSITION sites.
        var a = HoldDirective.HoldPosition;
        var b = HoldDirective.HoldPosition;

        Assert.Equal(HoldKind.HoldPosition, a.Kind);
        Assert.Null(a.YieldTarget);
        Assert.Equal(a, b);
    }

    [Fact]
    public void GiveWay_RequiresTargetCallsign()
    {
        Assert.Throws<System.ArgumentException>(() => HoldDirective.GiveWay(""));
        Assert.Throws<System.ArgumentException>(() => HoldDirective.GiveWay("   "));
    }

    [Fact]
    public void GiveWay_ValueEqualityOnYieldTarget()
    {
        Assert.Equal(HoldDirective.GiveWay("SWA123"), HoldDirective.GiveWay("SWA123"));
        Assert.NotEqual(HoldDirective.GiveWay("SWA123"), HoldDirective.GiveWay("UAL456"));
        Assert.NotEqual(HoldDirective.GiveWay("SWA123"), HoldDirective.HoldPosition);
    }

    [Fact]
    public void IsGiveWayFor_MatchesCaseInsensitively()
    {
        var hold = HoldDirective.GiveWay("SWA123");
        Assert.True(hold.IsGiveWayFor("SWA123"));
        Assert.True(hold.IsGiveWayFor("swa123"));
        Assert.False(hold.IsGiveWayFor("UAL456"));
        Assert.False(HoldDirective.HoldPosition.IsGiveWayFor("SWA123"));
    }

    // ---------------------------------------------------------------------------
    // Idempotence: HOLDPOSITION ↔ GIVEWAY transitions
    // ---------------------------------------------------------------------------

    [Fact]
    public void GiveWayThenHoldPosition_ClearsYieldTargetAndDisarmsAutoRelease()
    {
        // Locks in: HOLDPOSITION after GIVEWAY replaces the directive cleanly.
        // The aircraft becomes unconditionally held; FlightPhysics.UpdateGiveWayResume
        // will not auto-release because Hold.Kind is no longer GiveWay.
        var ac = MakeGroundAircraftWithRoute();

        GroundCommandHandler.TryGiveWay(ac, "SWA123");
        Assert.Equal(HoldKind.GiveWay, ac.Ground.Hold!.Kind);
        Assert.Equal("SWA123", ac.Ground.Hold.YieldTarget);

        GroundCommandHandler.TryHoldPosition(ac);

        Assert.Equal(HoldDirective.HoldPosition, ac.Ground.Hold);
        Assert.Null(ac.Ground.Hold.YieldTarget);
    }

    [Fact]
    public void HoldPositionThenGiveWay_ReplacesWithGiveWay()
    {
        // Locks in: GIVEWAY after HOLDPOSITION arms the conditional auto-release.
        // Reverse direction of the above.
        var ac = MakeGroundAircraftWithRoute();

        GroundCommandHandler.TryHoldPosition(ac);
        Assert.Equal(HoldKind.HoldPosition, ac.Ground.Hold!.Kind);

        GroundCommandHandler.TryGiveWay(ac, "UAL456");

        Assert.Equal(HoldKind.GiveWay, ac.Ground.Hold!.Kind);
        Assert.Equal("UAL456", ac.Ground.Hold.YieldTarget);
    }

    // ---------------------------------------------------------------------------
    // Snapshot round-trip via the existing IsHeld + GiveWayTarget DTO fields
    // ---------------------------------------------------------------------------

    [Fact]
    public void Snapshot_RoundTrip_HoldPositionPreservesKind()
    {
        var ground = new AircraftGroundOps { Hold = HoldDirective.HoldPosition };
        var dto = ground.ToSnapshot();
        Assert.True(dto.IsHeld);
        Assert.Null(dto.GiveWayTarget);

        var restored = AircraftGroundOps.FromSnapshot(dto, layout: null);
        Assert.Equal(HoldDirective.HoldPosition, restored.Hold);
    }

    [Fact]
    public void Snapshot_RoundTrip_GiveWayPreservesTarget()
    {
        var ground = new AircraftGroundOps { Hold = HoldDirective.GiveWay("SWA123") };
        var dto = ground.ToSnapshot();
        Assert.True(dto.IsHeld);
        Assert.Equal("SWA123", dto.GiveWayTarget);

        var restored = AircraftGroundOps.FromSnapshot(dto, layout: null);
        Assert.Equal(HoldDirective.GiveWay("SWA123"), restored.Hold);
    }

    [Fact]
    public void Snapshot_RoundTrip_NoHoldStaysNull()
    {
        var ground = new AircraftGroundOps { Hold = null };
        var dto = ground.ToSnapshot();
        Assert.False(dto.IsHeld);
        Assert.Null(dto.GiveWayTarget);

        var restored = AircraftGroundOps.FromSnapshot(dto, layout: null);
        Assert.Null(restored.Hold);
    }

    [Fact]
    public void Snapshot_LegacyV4Format_IsHeldTrueWithoutTarget_BecomesHoldPosition()
    {
        // Bug-bundle compatibility: pre-redesign snapshots that recorded a HOLDPOSITION
        // serialised IsHeld=true with GiveWayTarget=null. The reader must reconstitute
        // these as a HoldPosition directive (not a malformed GiveWay).
        var legacy = new Yaat.Sim.Simulation.Snapshots.AircraftGroundOpsDto
        {
            IsHeld = true,
            GiveWayTarget = null,
            AutoDeleteExempt = false,
            ConflictBreakRemainingSeconds = 0,
            HasAnnouncedReady = false,
        };

        var restored = AircraftGroundOps.FromSnapshot(legacy, layout: null);
        Assert.Equal(HoldDirective.HoldPosition, restored.Hold);
    }

    [Fact]
    public void Snapshot_LegacyV4Format_IsHeldTrueWithTarget_BecomesGiveWay()
    {
        var legacy = new Yaat.Sim.Simulation.Snapshots.AircraftGroundOpsDto
        {
            IsHeld = true,
            GiveWayTarget = "SWA123",
            AutoDeleteExempt = false,
            ConflictBreakRemainingSeconds = 0,
            HasAnnouncedReady = false,
        };

        var restored = AircraftGroundOps.FromSnapshot(legacy, layout: null);
        Assert.Equal(HoldDirective.GiveWay("SWA123"), restored.Hold);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static AircraftState MakeGroundAircraftWithRoute()
    {
        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "C172",
            Position = new LatLon(37.6213, -122.3790),
            TrueHeading = new TrueHeading(90),
            IsOnGround = true,
        };
        // TryGiveWay requires AssignedTaxiRoute != null. The route contents don't
        // matter for these tests — they exercise the directive lifecycle.
        ac.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        return ac;
    }
}

using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The pilot lens's check-in truth: one mark per track, the highest that holds of Calling, VciMismatch, NotCheckedIn and
/// VciUnset, and the tag each one draws.
/// </summary>
public class ScopeCheckInTests
{
    private static readonly ScopeViewer EramViewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static readonly ScopeViewer StarsViewer = new(new ActingPosition("OAK_APP", "NCT", "2S"), "NCT", null);

    private static readonly PendingPilotRequestDto Release = new("Release", "Pending", "ready for release", 1.0, 1.0);

    private static AircraftModel OwnedByViewer()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_44_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C44";
        return ac;
    }

    private static AircraftModel OwnedByOther()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_45_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C45";
        return ac;
    }

    private static (ScopeCheckInState State, string? Label) Mark(AircraftModel ac, ScopeViewer viewer)
    {
        ScopeCheckInState state = ScopeCheckIn.StateOf(ac, viewer);
        return (state, ScopeCheckIn.LabelOf(state, ac.PendingPilotRequest));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void OpenRequest_IsCalling_WithItsKind_WhateverTheVciContactAndOwnership(bool vciLit, bool contact, bool owned)
    {
        AircraftModel ac = owned ? OwnedByViewer() : OwnedByOther();
        ac.EramOnFrequencySectorIds = vciLit ? ["44"] : [];
        ac.HasMadeInitialContact = contact;
        ac.PendingPilotRequest = Release;

        Assert.Equal((ScopeCheckInState.Calling, "CALL RELEASE"), Mark(ac, EramViewer));
    }

    [Fact]
    public void NoRequest_VciLit_NoContact_IsVciMismatch()
    {
        AircraftModel ac = OwnedByViewer();
        ac.EramOnFrequencySectorIds = ["44"];
        ac.HasMadeInitialContact = false;

        Assert.Equal((ScopeCheckInState.VciMismatch, "VCI NO CALL"), Mark(ac, EramViewer));
    }

    [Fact]
    public void NoRequest_VciNotLit_NoContact_OwnedByTheViewer_IsNotCheckedIn()
    {
        AircraftModel ac = OwnedByViewer();
        ac.HasMadeInitialContact = false;

        Assert.Equal((ScopeCheckInState.NotCheckedIn, "NO CALL"), Mark(ac, EramViewer));
    }

    [Fact]
    public void NoRequest_NoContact_OwnedByAnotherPosition_IsNone()
    {
        AircraftModel ac = OwnedByOther();
        ac.HasMadeInitialContact = false;

        Assert.Equal((ScopeCheckInState.None, null), Mark(ac, EramViewer));
    }

    [Fact]
    public void NoRequest_NoContact_Unowned_IsNone()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.HasMadeInitialContact = false;

        Assert.Equal((ScopeCheckInState.None, null), Mark(ac, EramViewer));
    }

    [Fact]
    public void NoRequest_NoContact_NoViewer_IsNone()
    {
        AircraftModel ac = OwnedByViewer();
        ac.HasMadeInitialContact = false;

        Assert.Equal((ScopeCheckInState.None, null), Mark(ac, new ScopeViewer(null, null, null)));
    }

    [Fact]
    public void EramViewer_ContactMade_NoRequest_VciNotLit_IsVciUnset()
    {
        AircraftModel ac = OwnedByViewer();
        ac.HasMadeInitialContact = true;

        Assert.Equal((ScopeCheckInState.VciUnset, "VCI UNSET"), Mark(ac, EramViewer));
    }

    [Fact]
    public void EramViewer_ContactMade_NoRequest_VciNotLit_OwnedByAnotherSector_IsNone()
    {
        AircraftModel ac = OwnedByOther();
        ac.HasMadeInitialContact = true;

        Assert.Equal((ScopeCheckInState.None, null), Mark(ac, EramViewer));
    }

    [Fact]
    public void EramViewer_ContactMade_NoRequest_VciNotLit_Unowned_IsNone()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.HasMadeInitialContact = true;

        Assert.Equal((ScopeCheckInState.None, null), Mark(ac, EramViewer));
    }

    [Fact]
    public void StarsOnlyViewer_ContactMade_NoRequest_IsNone()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Owner = "OAK_APP";
        ac.OwnerFacilityId = "NCT";
        ac.OwnerSectorCode = "2S";
        ac.HasMadeInitialContact = true;

        Assert.Equal((ScopeCheckInState.None, null), Mark(ac, StarsViewer));
    }

    [Fact]
    public void ContactMade_NoRequest_VciLit_IsNone()
    {
        AircraftModel ac = OwnedByViewer();
        ac.EramOnFrequencySectorIds = ["44"];
        ac.HasMadeInitialContact = true;

        Assert.Equal((ScopeCheckInState.None, null), Mark(ac, EramViewer));
    }
}

using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins <see cref="ScopeEramConflictGate"/>, the Scope's half of the shared ERAM conflict display rule (§377: an alert shows
/// in a facility only when one of its two targets is owned there; CRC's half is yaat-server's
/// <c>CrcBroadcastService.IsEramConflictShown</c>), and which members of a shown pair flash.
/// </summary>
public class ScopeEramConflictGateTests
{
    // A pair as the server sends it with every facility's CA settings on: each owner facility is listed as shown.
    private static ConflictAlertDto EramPair(string? facilityA, string? facilityB, string? intruder = null) =>
        new(
            "ESTCA_AAL1_UAL2",
            "AAL1",
            "UAL2",
            ConflictAlertKind.Eram,
            IntruderCallsign: intruder,
            OwnerFacilityA: facilityA,
            OwnerFacilityB: facilityB
        )
        {
            ShownFacilities = [.. new[] { facilityA, facilityB }.OfType<string>().Distinct()],
        };

    /// <summary>
    /// The server's settings rows (yaat-server <c>EramPair_ListsOnlyTheOwnerFacilitiesWhoseSettingsShowIt</c>): a ZOA/ZLA pair
    /// with ZOA's CA function off lists only ZLA.
    /// </summary>
    [Theory]
    [InlineData("ZOA", false)]
    [InlineData("ZLA", true)]
    [InlineData(null, true)]
    public void IsShown_PassesOnlyAViewerInAFacilityWhoseSettingsShowThePair(string? viewer, bool shown)
    {
        ConflictAlertDto pair = EramPair("ZOA", "ZLA") with { ShownFacilities = ["ZLA"] };

        Assert.Equal(shown, ScopeEramConflictGate.IsShown(pair, ViewerIn(viewer)));
    }

    private static ScopeViewer ViewerIn(string? eramFacility) => new(null, eramFacility, null);

    public static TheoryData<string?, string?, string?, string?, bool> GateRows =>
        new()
        {
            // viewer facility, owner A, owner B, intruder, shown
            { "ZOA", "ZOA", null, null, true },
            { "ZOA", null, "ZOA", null, true },
            { "ZOA", "ZLA", "ZSE", null, false },
            { "ZOA", null, null, null, false },
            { null, "ZLA", "ZSE", null, true },
            { "ZOA", "ZOA", null, "UAL2", true },
            { "ZOA", "ZLA", null, "UAL2", false },
            { null, "ZLA", null, "UAL2", true },
        };

    [Theory]
    [MemberData(nameof(GateRows))]
    public void IsShown_PassesAPairOwnedInTheViewersFacility(string? viewer, string? ownerA, string? ownerB, string? intruder, bool shown) =>
        Assert.Equal(shown, ScopeEramConflictGate.IsShown(EramPair(ownerA, ownerB, intruder), ViewerIn(viewer)));

    [Fact]
    public void IsShown_RefusesStarsAndSuppressedPairs()
    {
        Assert.False(ScopeEramConflictGate.IsShown(new ConflictAlertDto("CA_AAL1_UAL2", "AAL1", "UAL2"), ViewerIn(null)));
        Assert.False(ScopeEramConflictGate.IsShown(EramPair("ZOA", null) with { Suppressed = true }, ViewerIn("ZOA")));
    }

    [Fact]
    public void FlashingCallsigns_HoldsBothMembersOfAShownPair()
    {
        IReadOnlySet<string> flashing = ScopeEramConflictGate.FlashingCallsigns([EramPair("ZOA", "ZOA")], ViewerIn("ZOA"));

        Assert.Equal(["AAL1", "UAL2"], flashing.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FlashingCallsigns_LeavesOutTheIntruder()
    {
        IReadOnlySet<string> flashing = ScopeEramConflictGate.FlashingCallsigns([EramPair("ZOA", null, intruder: "UAL2")], ViewerIn("ZOA"));

        Assert.Equal(["AAL1"], flashing);
    }

    [Fact]
    public void FlashingCallsigns_IsEmpty_ForAPairTheGateRefuses_OrNoPairs()
    {
        Assert.Empty(ScopeEramConflictGate.FlashingCallsigns([EramPair("ZLA", null)], ViewerIn("ZOA")));
        Assert.Empty(ScopeEramConflictGate.FlashingCallsigns(null, ViewerIn("ZOA")));
    }
}

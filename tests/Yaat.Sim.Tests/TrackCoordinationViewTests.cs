using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="TrackCoordinationView"/> compares the track identities <c>AircraftUpdated</c> carries with the position a client
/// acts as. <see cref="TrackCoordinationView.MatchesActing"/> must judge the same pairs as <see cref="TrackOwner.MatchesPosition"/>,
/// the sim's own judge, and the three direction predicates read it.
/// </summary>
public class TrackCoordinationViewTests
{
    private static readonly TrackOwner OakTwr = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
    private static readonly TrackOwner OakGnd = TrackOwner.CreateStars("OAK_GND", "NCT", 3, "O");
    private static readonly TrackOwner Nct2S = TrackOwner.CreateStars("NCT_2S_APP", "NCT", 2, "S");
    private static readonly TrackOwner Nct4R = TrackOwner.CreateStars("NCT_4R_APP", "NCT", 4, "R");

    private static readonly ActingPosition ActingAs2S = Acting(Nct2S);
    private static readonly TrackPositionIdentity None = new(null, null, null);

    public static TheoryData<string, TrackOwner, TrackOwner, bool, bool> Pairs() =>
        new()
        {
            { "same callsign, same facility", OakTwr, OakTwr, false, true },
            {
                "same callsign, different facility",
                TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O"),
                TrackOwner.CreateStars("OAK_TWR", "OAK", 1, "T"),
                false,
                false
            },
            { "OAK_TWR and OAK_GND share 3O in one facility", OakTwr, OakGnd, false, true },
            {
                "one callsign in two facilities, same TCP",
                TrackOwner.CreateStars("NCT_2S_APP", "NCT", 2, "S"),
                TrackOwner.CreateStars("NCT_2S_APP", "SCT", 2, "S"),
                false,
                false
            },
            { "null acting callsign compares on the TCP", TrackOwner.CreateStars("NCT_2S_RELIEF", "NCT", 2, "S"), Nct2S, true, true },
            { "null acting callsign, other TCP", Nct4R, Nct2S, true, false },
            { "sector code mismatch", Nct4R, Nct2S, false, false },
            { "non-NAS position by callsign", TrackOwner.CreateNonNas("OAK_DEL"), TrackOwner.CreateNonNas("OAK_DEL"), false, true },
            {
                "ERAM sector shared by two callsigns",
                TrackOwner.CreateEram("OAK_40_CTR", "ZOA", "40"),
                TrackOwner.CreateEram("OAK_41_CTR", "ZOA", "40"),
                false,
                false
            },
        };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void MatchesActing_AgreesWithTrackOwnerMatchesPosition(
        string label,
        TrackOwner track,
        TrackOwner acting,
        bool actingCallsignUnknown,
        bool expected
    )
    {
        ActingPosition actingPosition = actingCallsignUnknown ? Acting(acting) with { Callsign = null } : Acting(acting);

        bool sim = acting.MatchesPosition(track);
        bool view = TrackCoordinationView.MatchesActing(track.Callsign, track.FacilityId, SectorCode(track), actingPosition);

        Assert.True(sim == expected, $"{label}: TrackOwner.MatchesPosition returned {sim}");
        Assert.True(view == expected, $"{label}: TrackCoordinationView.MatchesActing returned {view}");
    }

    [Fact]
    public void IsHandoffToActing_PeerOrRedirectedByMatches()
    {
        Assert.True(TrackCoordinationView.IsHandoffToActing(Identity(Nct2S), None, ActingAs2S));
        Assert.True(TrackCoordinationView.IsHandoffToActing(Identity(Nct4R), Identity(Nct2S), ActingAs2S));
        Assert.False(TrackCoordinationView.IsHandoffToActing(Identity(Nct4R), None, ActingAs2S));
        Assert.False(TrackCoordinationView.IsHandoffToActing(None, None, ActingAs2S));
    }

    [Fact]
    public void IsHandoffFromActing_OwnerMatchesWithAPeerSet()
    {
        Assert.True(TrackCoordinationView.IsHandoffFromActing(Identity(Nct2S), Identity(Nct4R), ActingAs2S));

        // An unstaffed peer names no callsign; its sector code still carries the handoff.
        TrackPositionIdentity unstaffedPeer = new(null, "NCT", "4R");
        Assert.True(TrackCoordinationView.IsHandoffFromActing(Identity(Nct2S), unstaffedPeer, ActingAs2S));
        Assert.False(TrackCoordinationView.IsHandoffFromActing(Identity(Nct4R), Identity(OakTwr), ActingAs2S));
    }

    [Fact]
    public void IsHandoffFromActing_NoPeer_IsFalse() => Assert.False(TrackCoordinationView.IsHandoffFromActing(Identity(Nct2S), None, ActingAs2S));

    [Fact]
    public void IsPointoutToActing_RecipientCodeMatches()
    {
        Assert.True(TrackCoordinationView.IsPointoutToActing("2S", ActingAs2S));
        Assert.False(TrackCoordinationView.IsPointoutToActing("4R", ActingAs2S));
        Assert.False(TrackCoordinationView.IsPointoutToActing(null, ActingAs2S));
    }

    [Fact]
    public void ThirdParty_NoneMatch()
    {
        // A handoff from 4R to 3O with a pointout from 4R to 3O, seen by a client acting as 2S.
        Assert.False(TrackCoordinationView.IsHandoffToActing(Identity(OakTwr), None, ActingAs2S));
        Assert.False(TrackCoordinationView.IsHandoffFromActing(Identity(Nct4R), Identity(OakTwr), ActingAs2S));
        Assert.False(TrackCoordinationView.IsPointoutToActing("3O", ActingAs2S));
    }

    /// <summary>
    /// A tower cab acts under its track facility, the parent TRACON its tracks carry (OAK_TWR owns tracks as NCT 3O), not the
    /// facility its position is listed under (OAK): only the track facility lets a handoff to or from it match.
    /// </summary>
    [Fact]
    public void TowerActingUnderItsTrackFacility_MatchesItsTracks_TheListingFacilityDoesNot()
    {
        var trackFacility = new ActingPosition("OAK_TWR", "NCT", "3O");
        var listingFacility = new ActingPosition("OAK_TWR", "OAK", "3O");

        Assert.True(TrackCoordinationView.IsHandoffToActing(Identity(OakTwr), None, trackFacility));
        Assert.True(TrackCoordinationView.IsHandoffFromActing(Identity(OakTwr), Identity(Nct2S), trackFacility));
        Assert.False(TrackCoordinationView.IsHandoffToActing(Identity(OakTwr), None, listingFacility));
        Assert.False(TrackCoordinationView.IsHandoffFromActing(Identity(OakTwr), Identity(Nct2S), listingFacility));
    }

    [Fact]
    public void PointoutRecipientCode_MatchesTheActingTcp_IgnoringCase() =>
        Assert.True(TrackCoordinationView.IsPointoutToActing("3o", new ActingPosition(null, "NCT", "3O")));

    private static ActingPosition Acting(TrackOwner owner) => new(owner.Callsign, owner.FacilityId, TrackResolver.AsPrefixCode(owner));

    private static TrackPositionIdentity Identity(TrackOwner owner) => new(owner.Callsign, owner.FacilityId, SectorCode(owner));

    // The sector code AircraftUpdated carries (the server's DtoConverter.FormatSectorCode).
    private static string? SectorCode(TrackOwner owner) =>
        owner switch
        {
            { SectorId: null } => null,
            { OwnerType: TrackOwnerType.Eram, Subset: null } => $"C{owner.SectorId}",
            { Subset: null } => null,
            _ => $"{owner.Subset}{owner.SectorId}",
        };
}

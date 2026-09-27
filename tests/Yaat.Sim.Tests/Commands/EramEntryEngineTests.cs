using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="EramEntryEngine"/> is the one body for the ERAM keyboard entries that write per-track ERAM state: the
/// live CRC handler and a replay of the recorded entry both apply through it. Each grammar form is pinned here, with
/// the guards the live handler used to enforce inline (the unforced <c>TRACK</c> owner check) and the canonical stored
/// forms CRC re-parses (H-prefixed headings, bare-digit speeds).
/// </summary>
public class EramEntryEngineTests
{
    private static readonly TrackOwner Sector44 = TrackOwner.CreateEram("ZOA_44_CTR", "ZOA", "44");
    private static readonly TrackOwner Sector45 = TrackOwner.CreateEram("ZOA_45_CTR", "ZOA", "45");

    private static readonly TrackOwner Boulder = TrackOwner.CreateStars("NCT_B", "NCT", 2, "B");

    private static CommandResult Apply(AircraftState ac, string entry, TrackOwner? identity) =>
        EramEntryEngine.Apply(ac, entry, new EramEntryContext(identity, Scenario: null, Redirect: null));

    /// <summary>A scenario whose one ATC position, Boulder, answers the TCP code <c>2B</c> a handoff names.</summary>
    private static EramEntryContext HandoffContext(TrackOwner identity) =>
        new(
            identity,
            new SimScenarioState
            {
                ScenarioId = "s",
                ScenarioName = "s",
                RngSeed = 0,
                OriginalScenarioJson = "{}",
                ElapsedSeconds = 30,
                AtcPositions =
                [
                    new ResolvedAtcPosition
                    {
                        Source = new ScenarioAtc { Id = Boulder.Callsign },
                        Owner = Boulder,
                        Tcp = new Tcp(2, "B", "tcp-2b", null),
                    },
                ],
            },
            Redirect: null
        );

    private static AircraftState Aircraft() =>
        new()
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            Altitude = 11_250,
            IndicatedAirspeed = 280,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan(),
        };

    [Fact]
    public void Track_TakesTheTrack_ClearsTheHandoff_AndUnfreezes()
    {
        AircraftState ac = Aircraft();
        ac.Track.HandoffPeer = Sector45;
        ac.Track.HandoffInitiatedAt = 12;
        ac.Eram.IsFrozen = true;
        ac.Eram.FrozenLat = 37.0;
        ac.Eram.FrozenLon = -122.0;
        ac.Eram.FrozenAltitude = 110;

        CommandResult result = Apply(ac, "TRACK", Sector44);

        Assert.True(result.Success, result.Message);
        Assert.Same(Sector44, ac.Track.Owner);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.Null(ac.Track.HandoffInitiatedAt);
        Assert.Null(ac.Track.HandoffRedirectedBy);
        Assert.False(ac.Eram.IsFrozen);
        Assert.Null(ac.Eram.FrozenLat);
        Assert.Null(ac.Eram.FrozenLon);
        Assert.Null(ac.Eram.FrozenAltitude);
    }

    [Theory]
    [InlineData("TRACK", false)]
    [InlineData("TRACK /OK", true)]
    public void Track_OnAnotherSectorsTrack_IsRefusedUnlessForced(string entry, bool taken)
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector45;

        CommandResult result = Apply(ac, entry, Sector44);

        Assert.Equal(taken, result.Success);
        Assert.Same(taken ? Sector44 : Sector45, ac.Track.Owner);
        if (!taken)
        {
            Assert.Equal(EramEntryErrors.AlreadyTracked, result.Message);
        }
    }

    [Fact]
    public void Track_WithoutAnIdentity_IsRefused()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "TRACK", null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.SessionNotActive, result.Message);
        Assert.Null(ac.Track.Owner);
    }

    [Fact]
    public void Freeze_ParksTheTrack_AndSnapshotsTheAltitude()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "FREEZE 37.25 -121.75", null);

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Eram.IsFrozen);
        Assert.Equal(37.25, ac.Eram.FrozenLat);
        Assert.Equal(-121.75, ac.Eram.FrozenLon);
        Assert.Equal(112, ac.Eram.FrozenAltitude);
    }

    [Theory]
    [InlineData("FREEZE")]
    [InlineData("FREEZE 37.25")]
    [InlineData("FREEZE north west")]
    public void Freeze_WithoutACoordinate_IsRefused(string entry)
    {
        AircraftState ac = Aircraft();

        Assert.False(Apply(ac, entry, null).Success);
        Assert.False(ac.Eram.IsFrozen);
    }

    [Theory]
    [InlineData("QQ 110", 110, null, null, null, "QQ 110 UAL1")]
    [InlineData("QQ R110", 110, null, null, 110, "QQ R110 UAL1")]
    [InlineData("QQ L090", null, 90, null, null, "QQ L90 UAL1")]
    [InlineData("QQ P070", null, null, 70, null, "QQ P70 UAL1")]
    public void Qq_SetsTheAltitudeTier(string entry, int? interim, int? local, int? procedure, int? cera, string message)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(message, result.Message);
        Assert.Equal(interim, ac.Eram.InterimAltitude);
        Assert.Equal(local, ac.Eram.LocalInterimAltitude);
        Assert.Equal(procedure, ac.Eram.ProcedureAltitude);
        Assert.Equal(cera, ac.Eram.ControllerEnteredAltitude);
    }

    [Fact]
    public void Qq_InterimAndProcedure_AreMutuallyExclusive()
    {
        AircraftState ac = Aircraft();

        Apply(ac, "QQ 110", null);
        Apply(ac, "QQ P070", null);

        Assert.Null(ac.Eram.InterimAltitude);
        Assert.Equal(70, ac.Eram.ProcedureAltitude);
    }

    [Fact]
    public void Qq_Bare_ClearsInterimAndProcedure_AndQqL_ClearsTheLocalOnly()
    {
        AircraftState ac = Aircraft();
        ac.Eram.InterimAltitude = 110;
        ac.Eram.ProcedureAltitude = 70;
        ac.Eram.LocalInterimAltitude = 90;

        Assert.True(Apply(ac, "QQ", null).Success);
        Assert.Null(ac.Eram.InterimAltitude);
        Assert.Null(ac.Eram.ProcedureAltitude);
        Assert.Equal(90, ac.Eram.LocalInterimAltitude);

        Assert.True(Apply(ac, "QQ L", null).Success);
        Assert.Null(ac.Eram.LocalInterimAltitude);
    }

    [Fact]
    public void Qq_WithNoNumericToken_IsRefused()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "QQ ABC", null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.AltFormat, result.Message);
    }

    [Fact]
    public void Qr_SetsTheControllerEnteredAltitude()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "QR 250", null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("QR 250 UAL1", result.Message);
        Assert.Equal(250, ac.Eram.ControllerEnteredAltitude);
        Assert.Null(ac.Eram.InterimAltitude);
    }

    [Theory]
    [InlineData("QR")]
    [InlineData("QR 0")]
    [InlineData("QR X")]
    public void Qr_WithoutAPositiveAltitude_IsRefused(string entry) => Assert.False(Apply(Aircraft(), entry, null).Success);

    [Theory]
    [InlineData("QS 090", "H090")]
    [InlineData("QS 5", "H005")]
    [InlineData("QS 360", "H360")]
    [InlineData("QS h270", "H270")]
    [InlineData("QS 20L", "20L")]
    [InlineData("QS 5R", "5R")]
    public void Qs_Heading_StoresTheCanonicalForm(string entry, string stored)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(stored, ac.Eram.AssignedHeading);
        Assert.Equal($"QS {stored} UAL1", result.Message);
    }

    [Theory]
    [InlineData("QS 000")]
    [InlineData("QS 361")]
    [InlineData("QS 0L")]
    [InlineData("QS ABC")]
    public void Qs_BadHeading_IsRefused(string entry)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.HeadingFormat, result.Message);
        Assert.Null(ac.Eram.AssignedHeading);
    }

    [Theory]
    [InlineData("QS /250", "250")]
    [InlineData("QS /S250", "250")]
    [InlineData("QS /250+", "250+")]
    [InlineData("QS /m82", "M82")]
    [InlineData("QS /M100-", "M100-")]
    public void Qs_Speed_StoresTheCanonicalForm(string entry, string stored)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(stored, ac.Eram.AssignedSpeed);
        Assert.Equal($"QS /{stored} UAL1", result.Message);
    }

    [Theory]
    [InlineData("QS /80")]
    [InlineData("QS /099")]
    [InlineData("QS /M8")]
    [InlineData("QS /")]
    public void Qs_BadSpeed_IsRefused(string entry)
    {
        AircraftState ac = Aircraft();

        Assert.False(Apply(ac, entry, null).Success);
        Assert.Null(ac.Eram.AssignedSpeed);
    }

    [Theory]
    [InlineData("QS `expect", "EXPECT")]
    [InlineData("QS `A", "A")]
    [InlineData("QS `ils28r", "ILS28R")]
    [InlineData("QS `ABCDEFGH", "ABCDEFGH")]
    public void Qs_FreeText_OfOneToEightCharacters_IsStoredUpperCased(string entry, string stored)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(stored, ac.Eram.FreeText);
        Assert.Equal($"QS {stored} UAL1", result.Message);
    }

    [Theory]
    [InlineData("QS `ABCDEFGHI")] // nine characters
    [InlineData("QS `EXPECT ILS")] // an embedded space
    [InlineData("QS ` EXPECT")] // a leading space
    [InlineData("QS `ILS28R/2")] // a special character
    [InlineData("QS `*")] // a special character alone
    public void Qs_FreeText_OutsideOneToEightNonSpecialCharacters_IsRefused_AndKeepsTheOldText(string entry)
    {
        AircraftState ac = Aircraft();
        ac.Eram.FreeText = "KEEP";

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.TextFormat, result.Message);
        Assert.Equal("KEEP", ac.Eram.FreeText);
    }

    [Theory]
    [InlineData("QS * 270", "270")]
    [InlineData("QS */ /250", "/250")]
    [InlineData("QS /* `NOTE", "`NOTE")]
    [InlineData("QS 270 *", "*")]
    [InlineData("QS /250 /*", "/*")]
    public void Qs_ActionTypeWithHsfData_IsRefused_AndChangesNothing(string entry, string fieldInError)
    {
        AircraftState ac = Aircraft();
        ac.Eram.AssignedHeading = "H090";
        ac.Eram.AssignedSpeed = "300";
        ac.Eram.FreeText = "KEEP";

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal($"{EramEntryErrors.CofieFormat} {fieldInError}", result.Message);
        Assert.Equal("H090", ac.Eram.AssignedHeading);
        Assert.Equal("300", ac.Eram.AssignedSpeed);
        Assert.Equal("KEEP", ac.Eram.FreeText);
    }

    [Fact]
    public void Qs_EmptyFreeText_IsRefused()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "QS `", null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.TextFormat, result.Message);
    }

    [Theory]
    [InlineData("QS *", null, null, null)]
    [InlineData("QS */", null, "250", "TXT")]
    [InlineData("QS /*", "H270", null, "TXT")]
    public void Qs_DeleteForms_ClearTheNamedFields(string entry, string? heading, string? speed, string? text)
    {
        AircraftState ac = Aircraft();
        ac.Eram.AssignedHeading = "H270";
        ac.Eram.AssignedSpeed = "250";
        ac.Eram.FreeText = "TXT";

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(heading, ac.Eram.AssignedHeading);
        Assert.Equal(speed, ac.Eram.AssignedSpeed);
        Assert.Equal(text, ac.Eram.FreeText);
    }

    [Fact]
    public void Lf_SetsTheGroupLabel_AndBareLfClearsIt()
    {
        AircraftState ac = Aircraft();

        Assert.True(Apply(ac, "LF ABC", null).Success);
        Assert.Equal("ABC", ac.Eram.CrrGroupLabel);

        Assert.True(Apply(ac, "LF", null).Success);
        Assert.Null(ac.Eram.CrrGroupLabel);
    }

    [Fact]
    public void Vci_TogglesTheNamedSector_AndLeavesOtherSectorsAlone()
    {
        AircraftState ac = Aircraft();
        ac.Eram.OnFrequencySectorIds.Add("45");

        CommandResult on = Apply(ac, "VCI 44", null);

        Assert.True(on.Success, on.Message);
        Assert.Equal(["45", "44"], ac.Eram.OnFrequencySectorIds);

        CommandResult off = Apply(ac, "VCI 44", null);

        Assert.True(off.Success, off.Message);
        Assert.Equal(["45"], ac.Eram.OnFrequencySectorIds);
    }

    [Theory]
    [InlineData("VCI", EramEntryErrors.MessageTooShort)]
    [InlineData("VCI 44 45", EramEntryErrors.MessageTooLong)]
    public void Vci_WithoutExactlyOneSector_IsRefused(string entry, string error)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(error, result.Message);
        Assert.Empty(ac.Eram.OnFrequencySectorIds);
    }

    [Theory]
    [InlineData("LEADER D9", 9, 1)]
    [InlineData("LEADER L3", 4, 3)]
    [InlineData("LEADER D2 L0", 2, 0)]
    [InlineData("leader l2 d7", 7, 2)]
    [InlineData("LEADER L5", 4, 5)]
    public void Leader_SetsTheNamedFields_AndKeepsTheRest(string entry, int direction, int length)
    {
        AircraftState ac = Aircraft();
        ac.Eram.LeaderDirection = 4;
        ac.Eram.LeaderLength = 1;

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(direction, ac.Eram.LeaderDirection);
        Assert.Equal(length, ac.Eram.LeaderLength);
    }

    [Theory]
    [InlineData("LEADER", EramEntryErrors.MessageTooShort)]
    [InlineData("LEADER D0", "MsgInvalidDirection")]
    [InlineData("LEADER D10", "MsgInvalidDirection")]
    [InlineData("LEADER L4", "MsgInvalidLength")]
    [InlineData("LEADER D2 L9", "MsgInvalidLength")]
    [InlineData("LEADER D-1", "MsgCofieFormat D-1")]
    [InlineData("LEADER L+2", "MsgCofieFormat L+2")]
    [InlineData("LEADER DX", "MsgCofieFormat DX")]
    [InlineData("LEADER D3 X2", "MsgCofieFormat X2")]
    public void Leader_Malformed_IsRefused_AndChangesNothing(string entry, string message)
    {
        AircraftState ac = Aircraft();
        ac.Eram.LeaderDirection = 4;
        ac.Eram.LeaderLength = 1;

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Equal(4, ac.Eram.LeaderDirection);
        Assert.Equal(1, ac.Eram.LeaderLength);
    }

    [Fact]
    public void Handoff_ByANonOwner_IsRefusedNotYourControl_AndOffersNothing()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector45;

        CommandResult result = EramEntryEngine.Apply(ac, "HANDOFF 2B", HandoffContext(Sector44));

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.NotYourControl, result.Message);
        Assert.Same(Sector45, ac.Track.Owner);
        Assert.Null(ac.Track.HandoffPeer);
    }

    [Fact]
    public void Handoff_ByTheOwner_OffersTheTrack()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector44;

        CommandResult result = EramEntryEngine.Apply(ac, "HANDOFF 2B", HandoffContext(Sector44));

        Assert.True(result.Success, result.Message);
        Assert.Same(Sector44, ac.Track.Owner);
        Assert.Same(Boulder, ac.Track.HandoffPeer);
        Assert.Equal(30, ac.Track.HandoffInitiatedAt);
    }

    [Fact]
    public void Handoff_ByANonOwnerWithOk_OffersTheTrackOnTheOwnersBehalf()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector45;

        CommandResult result = EramEntryEngine.Apply(ac, "HANDOFF 2B /ok", HandoffContext(Sector44));

        Assert.True(result.Success, result.Message);
        Assert.Same(Sector45, ac.Track.Owner);
        Assert.Same(Boulder, ac.Track.HandoffPeer);
    }

    [Fact]
    public void Handoff_ByThePendingRecipient_IsRefusedNotYourControl()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector45;
        ac.Track.HandoffPeer = Sector44;

        CommandResult result = EramEntryEngine.Apply(ac, "HANDOFF 2B", HandoffContext(Sector44));

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.NotYourControl, result.Message);
        Assert.Same(Sector44, ac.Track.HandoffPeer);
        Assert.Null(ac.Track.HandoffRedirectedBy);
    }

    [Theory]
    [InlineData(false)] // another ARTCC's ERAM sector
    [InlineData(true)] // a STARS position
    public void Handoff_WithOk_OnATrackOwnedOutsideThisCentre_IsRefusedNotYourControl(bool starsOwner)
    {
        AircraftState ac = Aircraft();
        TrackOwner owner = starsOwner ? TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O") : TrackOwner.CreateEram("LAX_32_CTR", "ZLA", "32");
        ac.Track.Owner = owner;

        CommandResult result = EramEntryEngine.Apply(ac, "HANDOFF 2B /OK", HandoffContext(Sector44));

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.NotYourControl, result.Message);
        Assert.Same(owner, ac.Track.Owner);
        Assert.Null(ac.Track.HandoffPeer);
    }

    [Theory]
    [InlineData("HANDOFF", EramEntryErrors.MessageTooShort)]
    [InlineData("HANDOFF 2B /OK X", EramEntryErrors.MessageTooLong)]
    [InlineData("HANDOFF 2B OK", "MsgCofieFormat OK")]
    public void Handoff_Malformed_IsRefused(string entry, string message)
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector44;

        CommandResult result = EramEntryEngine.Apply(ac, entry, HandoffContext(Sector44));

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Null(ac.Track.HandoffPeer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("QZ 350")]
    [InlineData("HELLO")]
    public void UnknownEntry_IsRefused(string entry) => Assert.False(Apply(Aircraft(), entry, Sector44).Success);
}

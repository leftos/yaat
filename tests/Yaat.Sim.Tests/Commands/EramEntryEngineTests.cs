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
        EramEntryEngine.Apply(ac, entry, new EramEntryContext(identity, Scenario: null, Redirect: null, new EramConflictState()));

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
            Redirect: null,
            new EramConflictState()
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

    [Theory]
    [InlineData(false)] // another ARTCC's ERAM sector
    [InlineData(true)] // a STARS position
    public void Track_WithOk_OnATrackOwnedOutsideThisCentre_IsRefusedNotYourControl(bool starsOwner)
    {
        AircraftState ac = Aircraft();
        TrackOwner owner = starsOwner ? TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O") : TrackOwner.CreateEram("LAX_32_CTR", "ZLA", "32");
        ac.Track.Owner = owner;

        CommandResult result = Apply(ac, "TRACK /OK", Sector44);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.NotYourControl, result.Message);
        Assert.Same(owner, ac.Track.Owner);
    }

    [Theory]
    [InlineData(false)] // an ERAM sector
    [InlineData(true)] // a STARS position, which the override's same-centre rule alone would refuse
    public void Track_WithOk_ByTheOwningPosition_Succeeds(bool starsOwner)
    {
        AircraftState ac = Aircraft();
        TrackOwner owner = starsOwner ? TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O") : Sector44;
        ac.Track.Owner = owner;

        CommandResult result = Apply(ac, "TRACK /OK", owner);

        Assert.True(result.Success, result.Message);
        Assert.Same(owner, ac.Track.Owner);
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
    [InlineData("QQ L110", null, 110, null, null, "QQ L110 UAL1")]
    [InlineData("QQ P110", null, null, 110, null, "QQ P110 UAL1")]
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

    [Theory]
    [InlineData("QQ l110", null, 110, null)]
    [InlineData("QQ r110", 110, null, 110)]
    [InlineData("QQ p110", null, null, null)]
    public void Qq_LowerCasePrefix_IsField76(string entry, int? interim, int? local, int? cera)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(interim, ac.Eram.InterimAltitude);
        Assert.Equal(local, ac.Eram.LocalInterimAltitude);
        Assert.Equal(cera, ac.Eram.ControllerEnteredAltitude);
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("X")]
    public void Qq_LettersOtherThanL_AreAnIllegalAction(string token)
    {
        AircraftState ac = Aircraft();
        ac.Eram.LocalInterimAltitude = 90;

        CommandResult result = Apply(ac, $"QQ {token}", null);

        Assert.False(result.Success);
        Assert.Equal($"{EramEntryErrors.CofieIllegalAction} {token}", result.Message);
        Assert.Equal(90, ac.Eram.LocalInterimAltitude);
    }

    [Theory]
    [InlineData("QQ 000")]
    [InlineData("QQ L000")]
    [InlineData("QQ 5")]
    [InlineData("QQ 12")]
    [InlineData("QQ 1100")]
    [InlineData("QQ -5")]
    [InlineData("QQ 11A")]
    [InlineData("QQ R11")]
    [InlineData("QQ 110 120")]
    [InlineData("QQ L110 P070")]
    public void Qq_AnythingButOneAltitudeField_IsAnAltFormatError(string entry)
    {
        AircraftState ac = Aircraft();
        ac.Eram.InterimAltitude = 90;

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.AltFormat, result.Message);
        Assert.Equal(90, ac.Eram.InterimAltitude);
        Assert.Null(ac.Eram.LocalInterimAltitude);
        Assert.Null(ac.Eram.ProcedureAltitude);
    }

    [Theory]
    [InlineData("QQ L 110")]
    [InlineData("QQ L L")]
    public void Qq_DeletionIndicatorWithAnotherField_IsTooLong(string entry)
    {
        AircraftState ac = Aircraft();
        ac.Eram.LocalInterimAltitude = 90;

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.MessageTooLong, result.Message);
        Assert.Equal(90, ac.Eram.LocalInterimAltitude);
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
    public void Qr_MissingOrMalformedAltitude_IsRefused(string entry) => Assert.False(Apply(Aircraft(), entry, null).Success);

    [Theory]
    [InlineData("QR 5")]
    [InlineData("QR 1234")]
    [InlineData("QR 12A")]
    [InlineData("QR JUNK 120")]
    [InlineData("QR 120 130")]
    public void Qr_AnythingButOneThreeDigitField_IsAnAltFormatError(string entry)
    {
        AircraftState ac = Aircraft();
        ac.Eram.ControllerEnteredAltitude = 90;

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.AltFormat, result.Message);
        Assert.Equal(90, ac.Eram.ControllerEnteredAltitude);
    }

    [Fact]
    public void Qr_WithALeadingZero_SetsTheAltitude()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "QR 050", null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(50, ac.Eram.ControllerEnteredAltitude);
    }

    [Fact]
    public void Qr_Zero_ClearsTheControllerEnteredAltitude()
    {
        AircraftState ac = Aircraft();
        ac.Eram.ControllerEnteredAltitude = 250;

        CommandResult result = Apply(ac, "QR 000", null);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Eram.ControllerEnteredAltitude);
    }

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
    [InlineData("QS /250", "S250")]
    [InlineData("QS /s250", "S250")]
    [InlineData("QS /S250", "S250")]
    [InlineData("QS /075", "S075")]
    [InlineData("QS /250+", "250+")]
    [InlineData("QS /250-", "250-")]
    [InlineData("QS /78", "M78")]
    [InlineData("QS /78+", "M78+")]
    [InlineData("QS /78-", "M78-")]
    [InlineData("QS /M78", "M78")]
    [InlineData("QS /m82", "M82")]
    [InlineData("QS /M78+", "M78+")]
    [InlineData("QS /M78-", "M78-")]
    [InlineData("QS /M.78", "M78")]
    [InlineData("QS /.78", "M78")]
    [InlineData("QS /.78+", "M78+")]
    [InlineData("QS /.78-", "M78-")]
    [InlineData("QS /+50", "+50")]
    [InlineData("QS /+5", "+5")]
    [InlineData("QS /-50", "-50")]
    [InlineData("QS /-5", "-5")]
    [InlineData("QS /PS", "PS")]
    [InlineData("QS /ps", "PS")]
    [InlineData("QS /+", "+")]
    [InlineData("QS /-", "-")]
    public void Qs_Speed_StoresTheCanonicalForm(string entry, string stored)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(stored, ac.Eram.AssignedSpeed);
        Assert.Equal($"QS /{stored} UAL1", result.Message);
    }

    [Theory]
    [InlineData("QS /")]
    [InlineData("QS /000")]
    [InlineData("QS /0")]
    [InlineData("QS /5")]
    [InlineData("QS /2500")]
    [InlineData("QS /M8")]
    [InlineData("QS /M100")]
    [InlineData("QS /M100-")]
    [InlineData("QS /M.7")]
    [InlineData("QS /M.78+")]
    [InlineData("QS /.7")]
    [InlineData("QS /78.5")]
    [InlineData("QS /S250+")]
    [InlineData("QS /S78")]
    [InlineData("QS /S")]
    [InlineData("QS /M")]
    [InlineData("QS /+500")]
    [InlineData("QS /-500")]
    [InlineData("QS /+5+")]
    [InlineData("QS /PS+")]
    [InlineData("QS /abc")]
    [InlineData("QS /S+")]
    public void Qs_BadSpeed_IsRefused(string entry)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.SpeedFormat, result.Message);
        Assert.Null(ac.Eram.AssignedSpeed);
    }

    [Theory]
    [InlineData("QS 270/250", "H270", "S250")]
    [InlineData("QS 20L/M78", "20L", "M78")]
    [InlineData("QS H090/.78+", "H090", "M78+")]
    [InlineData("QS 270/PS", "H270", "PS")]
    public void Qs_HeadingAndSpeed_StoresBoth(string entry, string heading, string speed)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(heading, ac.Eram.AssignedHeading);
        Assert.Equal(speed, ac.Eram.AssignedSpeed);
        Assert.Equal($"QS {heading}/{speed} UAL1", result.Message);
    }

    [Theory]
    [InlineData("QS 270/", "270/")]
    [InlineData("QS 999/250", "999/250")]
    [InlineData("QS 270/abc", "270/abc")]
    [InlineData("QS 270/250/300", "270/250/300")]
    public void Qs_BadHeadingAndSpeed_IsRefused_AndStoresNeither(string entry, string fieldInError)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal($"{EramEntryErrors.CofieFormat} {fieldInError}", result.Message);
        Assert.Null(ac.Eram.AssignedHeading);
        Assert.Null(ac.Eram.AssignedSpeed);
    }

    [Theory]
    [InlineData("QS `expect", "EXPECT")]
    [InlineData("QS `A", "A")]
    [InlineData("QS `ils28r", "ILS28R")]
    [InlineData("QS `ABCDEFGH", "ABCDEFGH")]
    [InlineData("QS `ILS28R/2", "ILS28R/2")]
    [InlineData("QS `a-b+c=d", "A-B+C=D")]
    [InlineData("QS `*/_.,", "*/_.,")]
    [InlineData("QS `*", "*")]
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
    [InlineData("QS `ILS28R!")] // a character outside the field 155 set
    [InlineData("QS `!")] // such a character alone
    [InlineData("QS `A#B")]
    public void Qs_FreeText_OutsideOneToEightField155Characters_IsRefused_AndKeepsTheOldText(string entry)
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

    [Fact]
    public void Handoff_AfterARedirect_APlainReinitiateClearsTheRedirect()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector45;
        ac.Track.HandoffPeer = Sector44;
        SimScenarioState scenario = HandoffContext(Sector44).Scenario!;
        CommandResult redirected = TrackEngine.ApplyHandoff(ac, scenario, Sector44, "2B", redirect: null);
        Assert.True(redirected.Success, redirected.Message);
        Assert.Same(Sector44, ac.Track.HandoffRedirectedBy);

        CommandResult reinitiated = TrackEngine.ApplyHandoff(ac, scenario, Sector45, "2B", redirect: null);

        Assert.True(reinitiated.Success, reinitiated.Message);
        Assert.Same(Boulder, ac.Track.HandoffPeer);
        Assert.Null(ac.Track.HandoffRedirectedBy);
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

    [Fact]
    public void Pointout_AddsOnePointoutPerReceivingSector()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "PO ZOA 44 ZOA 45 ZOA 46", null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, ac.Eram.Pointouts.Count);
        Assert.All(ac.Eram.Pointouts, p => Assert.Equal(("ZOA", "44", "ZOA"), (p.OriginatingFacility, p.OriginatingSector, p.ReceivingFacility)));
        Assert.Equal(["45", "46"], ac.Eram.Pointouts.Select(p => p.ReceivingSector));
        Assert.All(ac.Eram.Pointouts, p => Assert.False(p.IsAcknowledged));
    }

    [Fact]
    public void Pointout_ToASectorAlreadyPointedOutTo_IsRefusedAndAddsNothing()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = Apply(ac, "PO ZOA 44 ZOA 46 ZOA 45", null);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.PoExists, result.Message);
        Assert.Single(ac.Eram.Pointouts);
    }

    [Theory]
    [InlineData("PO", EramEntryErrors.MessageTooShort)]
    [InlineData("PO ZOA 44", EramEntryErrors.MessageTooShort)]
    [InlineData("PO ZOA 44 ZOA", EramEntryErrors.MessageTooShort)]
    [InlineData("PO ZOA 44 ZOA 45 ZOA", "MsgCofieFormat ZOA")]
    [InlineData("PO ZOA 44 ZOA 45 ZLA 45", EramEntryErrors.PoExists)]
    public void Pointout_Malformed_IsRefusedAndAddsNothing(string entry, string message)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.Equal(message, result.Message);
        Assert.Empty(ac.Eram.Pointouts);
    }

    [Fact]
    public void PointoutAck_AcknowledgesTheNamedPointoutOnly()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45 ZOA 46", null).Success);

        CommandResult result = Apply(ac, "POACK ZOA 44 ZOA 46", null);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Eram.Pointouts[0].IsAcknowledged);
        Assert.True(ac.Eram.Pointouts[1].IsAcknowledged);
    }

    [Fact]
    public void PointoutClear_ClearsBothSidesOfTheNamedPointout()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = Apply(ac, "POCLEAR ZOA 44 ZOA 45", null);

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Eram.Pointouts[0].IsRSideCleared);
        Assert.True(ac.Eram.Pointouts[0].IsDSideCleared);
    }

    [Theory]
    [InlineData("POACK ZOA 44 ZOA 46", EramEntryErrors.PoNotFound)]
    [InlineData("POACK ZOA 44 ZLA 45", EramEntryErrors.PoNotFound)]
    [InlineData("POCLEAR ZOA 45 ZOA 44", EramEntryErrors.PoNotFound)]
    [InlineData("POACK ZOA 44 ZOA", EramEntryErrors.MessageTooShort)]
    [InlineData("POCLEAR ZOA 44 ZOA 45 X", EramEntryErrors.MessageTooLong)]
    public void PointoutAckOrClear_OfNoSuchPointout_IsRefusedAndChangesNothing(string entry, string error)
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = Apply(ac, entry, null);

        Assert.Equal(error, result.Message);
        EramPointoutState pointout = Assert.Single(ac.Eram.Pointouts);
        Assert.False(pointout.IsAcknowledged || pointout.IsRSideCleared || pointout.IsDSideCleared);
    }

    [Fact]
    public void PointoutConvert_TakesTheTrackForTheReceiver_AndRemovesOnlyThatPointout()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector44;
        ac.Track.HandoffPeer = Boulder;
        ac.Eram.IsFrozen = true;
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45 ZOA 46", null).Success);

        CommandResult result = EramEntryEngine.Apply(ac, "POCONVERT ZOA 44 ZOA 45", HandoffContext(Sector45));

        Assert.True(result.Success, result.Message);
        Assert.Same(Sector45, ac.Track.Owner);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.True(ac.Track.HandoffAccepted);
        Assert.False(ac.Eram.IsFrozen);
        Assert.Same(Sector44, ac.Eram.RecentHandoffPreviousOwner);
        Assert.False(ac.Eram.RecentHandoffWasForced);
        EramPointoutState kept = Assert.Single(ac.Eram.Pointouts);
        Assert.Equal("46", kept.ReceivingSector);
    }

    [Theory]
    [InlineData("POCONVERT ZOA 44 ZOA 46")]
    [InlineData("POCONVERT ZOA 45 ZOA 44")]
    [InlineData("POCONVERT ZOA 44 ZLA 45")]
    public void PointoutConvert_OfNoSuchPointout_IsRefusedPoNotFound_AndChangesNothing(string entry)
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector44;
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = EramEntryEngine.Apply(ac, entry, HandoffContext(Sector45));

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.PoNotFound, result.Message);
        Assert.Same(Sector44, ac.Track.Owner);
        Assert.Single(ac.Eram.Pointouts);
    }

    [Fact]
    public void PointoutConvert_OfAPointoutClearedOnTheRSide_IsRefusedPoNotFound()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector44;
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);
        ac.Eram.Pointouts[0].IsRSideCleared = true;

        CommandResult result = EramEntryEngine.Apply(ac, "POCONVERT ZOA 44 ZOA 45", HandoffContext(Sector45));

        Assert.Equal(EramEntryErrors.PoNotFound, result.Message);
        Assert.Same(Sector44, ac.Track.Owner);
        Assert.Single(ac.Eram.Pointouts);
    }

    [Fact]
    public void PointoutConvert_OfATrackTheReceiverAlreadyOwns_MarksNoPreviousOwner()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector45;
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = EramEntryEngine.Apply(ac, "POCONVERT ZOA 44 ZOA 45", HandoffContext(Sector45));

        Assert.True(result.Success, result.Message);
        Assert.Same(Sector45, ac.Track.Owner);
        Assert.Null(ac.Eram.RecentHandoffPreviousOwner);
        Assert.Null(ac.Eram.RecentHandoffAcceptedAtSeconds);
        Assert.Empty(ac.Eram.Pointouts);
    }

    [Fact]
    public void PointoutConvert_OfACoastedTrack_EndsTheCoast()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector44;
        ac.Eram.IsCoastTrack = true;
        ac.Eram.CoastStartSeconds = 10;
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = EramEntryEngine.Apply(ac, "POCONVERT ZOA 44 ZOA 45", HandoffContext(Sector45));

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Eram.IsCoastTrack);
        Assert.Null(ac.Eram.CoastStartSeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PointoutConvert_WithOkAndNoPointout_TakesTheTrack(bool owned)
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = owned ? Sector44 : null;

        CommandResult result = EramEntryEngine.Apply(ac, "POCONVERT /ok", HandoffContext(Sector45));

        Assert.True(result.Success, result.Message);
        Assert.Same(Sector45, ac.Track.Owner);
        Assert.True(ac.Track.HandoffAccepted);
        Assert.Equal(owned, ac.Eram.RecentHandoffWasForced);
        Assert.Empty(ac.Eram.Pointouts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PointoutConvert_WithOk_OnATrackOwnedOutsideThisCentre_IsRefusedNotYourControl(bool starsOwner)
    {
        AircraftState ac = Aircraft();
        TrackOwner owner = starsOwner ? Boulder : TrackOwner.CreateEram("LAX_10_CTR", "ZLA", "10");
        ac.Track.Owner = owner;

        CommandResult result = EramEntryEngine.Apply(ac, "POCONVERT /OK", HandoffContext(Sector45));

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.NotYourControl, result.Message);
        Assert.Same(owner, ac.Track.Owner);
    }

    [Theory]
    [InlineData("POCONVERT", EramEntryErrors.MessageTooShort)]
    [InlineData("POCONVERT ZOA 44 ZOA", EramEntryErrors.MessageTooShort)]
    [InlineData("POCONVERT ZOA 44 ZOA 45 X", EramEntryErrors.MessageTooLong)]
    [InlineData("POCONVERT OK", "MsgCofieFormat OK")]
    public void PointoutConvert_Malformed_IsRefusedAndChangesNothing(string entry, string message)
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector44;
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = EramEntryEngine.Apply(ac, entry, HandoffContext(Sector45));

        Assert.Equal(message, result.Message);
        Assert.Same(Sector44, ac.Track.Owner);
        Assert.Single(ac.Eram.Pointouts);
    }

    [Fact]
    public void PointoutConvert_WithoutASession_IsRefused()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);

        CommandResult result = Apply(ac, "POCONVERT ZOA 44 ZOA 45", Sector45);

        Assert.Equal(EramEntryErrors.SessionNotActive, result.Message);
        Assert.Null(ac.Track.Owner);
        Assert.Single(ac.Eram.Pointouts);
    }

    /// <summary>An ERAM conflict set holding one active alert between <c>UAL1</c> and <c>AAL2</c>.</summary>
    private static EramConflictState OneAlert(out EramActiveConflict alert)
    {
        var conflicts = new EramConflictState();
        alert = new EramActiveConflict
        {
            Id = "ESTCA_AAL2_UAL1",
            CallsignA = "AAL2",
            CallsignB = "UAL1",
        };
        conflicts.Conflicts[alert.Id] = alert;
        return conflicts;
    }

    private static CommandResult ApplyCo(AircraftState ac, string entry, EramConflictState conflicts) =>
        EramEntryEngine.Apply(ac, entry, new EramEntryContext(Sector44, Scenario: null, Redirect: null, conflicts));

    [Fact]
    public void Co_SuppressesTheAlert_AndASecondEntryRestoresIt()
    {
        AircraftState ac = Aircraft();
        EramConflictState conflicts = OneAlert(out EramActiveConflict alert);

        CommandResult suppressed = ApplyCo(ac, "CO AAL2", conflicts);

        Assert.True(suppressed.Success, suppressed.Message);
        Assert.True(alert.Suppressed);

        CommandResult restored = ApplyCo(ac, "CO AAL2", conflicts);

        Assert.True(restored.Success, restored.Message);
        Assert.False(alert.Suppressed);
    }

    [Theory]
    [InlineData("CO DAL3", EramEntryErrors.NoConflictAlert)]
    [InlineData("CO UAL1", EramEntryErrors.InvalidCombination)]
    [InlineData("CO", EramEntryErrors.MessageTooShort)]
    [InlineData("CO AAL2 DAL3", EramEntryErrors.MessageTooLong)]
    public void Co_Refused_LeavesTheAlertAlone(string entry, string error)
    {
        AircraftState ac = Aircraft();
        EramConflictState conflicts = OneAlert(out EramActiveConflict alert);

        CommandResult result = ApplyCo(ac, entry, conflicts);

        Assert.Equal(error, result.Message);
        Assert.False(alert.Suppressed);
    }

    [Theory]
    [InlineData("DRI J", 1)]
    [InlineData("DRI t", 2)]
    public void Dri_SetsTheNamedHalo(string entry, int halo)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(halo, ac.Eram.DriHaloType);
    }

    [Fact]
    public void BareDri_RemovesTheHalo()
    {
        AircraftState ac = Aircraft();
        ac.Eram.DriHaloType = 2;

        CommandResult result = Apply(ac, "DRI", null);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Eram.DriHaloType);
    }

    [Theory]
    [InlineData("DRI X", "MsgCofieFormat X")]
    [InlineData("DRI J T", EramEntryErrors.MessageTooLong)]
    public void Dri_Malformed_IsRefusedAndKeepsTheHalo(string entry, string message)
    {
        AircraftState ac = Aircraft();
        ac.Eram.DriHaloType = 1;

        CommandResult result = Apply(ac, entry, null);

        Assert.Equal(message, result.Message);
        Assert.Equal(1, ac.Eram.DriHaloType);
    }

    [Fact]
    public void Min_AddsTheSector_Idempotently()
    {
        AircraftState ac = Aircraft();

        CommandResult first = Apply(ac, "MIN ZOA 44", null);
        CommandResult second = Apply(ac, "MIN ZOA 44", null);
        CommandResult other = Apply(ac, "MIN ZOA 45", null);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.True(other.Success, other.Message);
        Assert.Equal([new EramSectorKey("ZOA", "44"), new EramSectorKey("ZOA", "45")], ac.Eram.PointoutMinimizedSectors);
        Assert.True(ac.Eram.IsPointoutMinimizedFor("ZOA", "44"));
        Assert.False(ac.Eram.IsPointoutMinimizedFor("ZLA", "44"));
    }

    [Fact]
    public void Fdb_TogglesTheSector()
    {
        AircraftState ac = Aircraft();

        Assert.True(Apply(ac, "FDB ZOA 44", null).Success);
        Assert.True(Apply(ac, "FDB ZOA 45", null).Success);
        Assert.Equal([new EramSectorKey("ZOA", "44"), new EramSectorKey("ZOA", "45")], ac.Eram.FdbOpenSectors);

        Assert.True(Apply(ac, "FDB ZOA 44", null).Success);
        Assert.Equal([new EramSectorKey("ZOA", "45")], ac.Eram.FdbOpenSectors);
        Assert.False(ac.Eram.IsFdbOpenFor("ZOA", "44"));
        Assert.True(ac.Eram.IsFdbOpenFor("ZOA", "45"));
    }

    [Theory]
    [InlineData("MIN ZOA", EramEntryErrors.MessageTooShort)]
    [InlineData("MIN ZOA 44 45", EramEntryErrors.MessageTooLong)]
    [InlineData("FDB", EramEntryErrors.MessageTooShort)]
    [InlineData("FDB ZOA 44 45", EramEntryErrors.MessageTooLong)]
    public void MinOrFdb_Malformed_IsRefusedAndChangesNothing(string entry, string message)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Empty(ac.Eram.PointoutMinimizedSectors);
        Assert.Empty(ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void Pointout_Refused_KeepsTheMinimize()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "PO ZOA 44 ZOA 45", null).Success);
        Assert.True(Apply(ac, "MIN ZOA 44", null).Success);
        Assert.True(Apply(ac, "MIN ZOA 45", null).Success);

        CommandResult result = Apply(ac, "PO ZOA 44 ZOA 45", null);

        Assert.Equal(EramEntryErrors.PoExists, result.Message);
        Assert.Equal([new EramSectorKey("ZOA", "44"), new EramSectorKey("ZOA", "45")], ac.Eram.PointoutMinimizedSectors);
    }

    [Fact]
    public void Pointout_ClearsMinimizeForTheInitiatorAndEveryReceiver()
    {
        AircraftState ac = Aircraft();
        foreach (string sector in new[] { "44", "45", "46", "47" })
        {
            Assert.True(Apply(ac, $"MIN ZOA {sector}", null).Success);
        }

        CommandResult result = Apply(ac, "PO ZOA 44 ZOA 45 ZOA 46", null);

        Assert.True(result.Success, result.Message);
        Assert.Equal([new EramSectorKey("ZOA", "47")], ac.Eram.PointoutMinimizedSectors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("QZ 350")]
    [InlineData("HELLO")]
    public void UnknownEntry_IsRefused(string entry) => Assert.False(Apply(Aircraft(), entry, Sector44).Success);

    // ─── COAST (QT Coast Track) ──────────────────────────────────────────

    private static readonly LatLon CoastAnchor = new(37.5, -122.0);

    [Fact]
    public void Coast_WithAHeading_TakesTheTrack_AndDeadReckonsFromTheAnchor()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "COAST T100 @37.5,-122 S360 A150 H90", Sector44);

        Assert.True(result.Success, result.Message);
        Assert.Same(Sector44, ac.Track.Owner);
        Assert.True(ac.Eram.IsCoastTrack);
        Assert.Equal(150, ac.Eram.CoastAltitude);
        Assert.Equal(360, ac.Eram.CoastSpeed);
        Assert.Empty(ac.Eram.CoastRoute);
        Assert.Equal(CoastAnchor, ac.Eram.CoastPositionAt(100));

        // 360 kt for 60 s is 6 nm on the entered heading, a magnetic heading flown as its true course.
        double trueCourse = MagneticDeclination.MagneticToTrue(90, CoastAnchor.Lat, CoastAnchor.Lon);
        LatLon expected = GeoMath.ProjectPoint(CoastAnchor, new TrueHeading(trueCourse), 6.0);
        Assert.True(GeoMath.DistanceNm(expected, ac.Eram.CoastPositionAt(160)!.Value) < 0.01);
    }

    [Fact]
    public void Coast_WithoutValues_AnchorsAtTheTarget_AtItsAltitude_AndTheFiledTas()
    {
        AircraftState ac = Aircraft();
        ac.FlightPlan.SetTrueAirspeed(450);

        Assert.True(Apply(ac, "COAST T0", Sector44).Success);

        Assert.Equal(ac.Position.Lat, ac.Eram.CoastLat);
        Assert.Equal(ac.Position.Lon, ac.Eram.CoastLon);
        Assert.Equal(112, ac.Eram.CoastAltitude);
        Assert.Equal(450, ac.Eram.CoastSpeed);
    }

    [Fact]
    public void Coast_WithoutAHeading_FliesTheRoute_ThenHoldsTheLastLegsCourse()
    {
        AircraftState ac = Aircraft();
        var anchor = new LatLon(37.7, -122.3);
        var first = new LatLon(37.7, -122.2);
        var turn = new LatLon(37.7, -122.0);
        var end = new LatLon(38.0, -122.0);

        // The anchor is short of the route's first fix, on the line of the first leg: the track flies to it.
        Assert.True(Apply(ac, "COAST T0 @37.7,-122.3 S600 R37.7,-122.2 R37.7,-122.0 R38.0,-122.0", Sector44).Success);
        Assert.Equal([first, turn, end], ac.Eram.CoastRoute);

        // 600 kt for 60 s is 10 nm: about 4.7 nm to the first fix, the rest east along the next leg.
        double beyondFirst = 10.0 - GeoMath.DistanceNm(anchor, first);
        LatLon expected = GeoMath.ProjectPoint(first, new TrueHeading(GeoMath.BearingTo(first, turn)), beyondFirst);
        Assert.True(GeoMath.DistanceNm(expected, ac.Eram.CoastPositionAt(60)!.Value) < 0.01);

        // Past the last fix the track holds the last leg's course.
        LatLon later = ac.Eram.CoastPositionAt(600)!.Value;
        Assert.True(later.Lat > end.Lat);
        Assert.Equal(end.Lon, later.Lon, 2);
    }

    [Fact]
    public void Coast_FromALocationPastTheNextFix_JoinsTheRouteAtTheLegItIsOn()
    {
        AircraftState ac = Aircraft();
        ac.Position = new LatLon(37.7, -122.2);

        Assert.True(Apply(ac, "COAST T0 @37.8,-122.0 S600 R37.7,-122.0 R38.0,-122.0", Sector44).Success);

        Assert.Equal([new LatLon(38.0, -122.0)], ac.Eram.CoastRoute);
    }

    [Fact]
    public void Coast_OnAFrozenTrack_StartsFromTheFrozenSpot_AndUnfreezes()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "FREEZE 37.25 -121.75", null).Success);
        ac.Altitude = 20_000;

        Assert.True(Apply(ac, "COAST T0", Sector44).Success);

        Assert.False(ac.Eram.IsFrozen);
        Assert.Null(ac.Eram.FrozenLat);
        Assert.Equal(37.25, ac.Eram.CoastLat);
        Assert.Equal(-121.75, ac.Eram.CoastLon);
        Assert.Equal(112, ac.Eram.CoastAltitude);
    }

    [Theory]
    [InlineData("FREEZE 37.25 -121.75", null)]
    [InlineData("TRACK", "ZOA_44_CTR")]
    public void Coast_EndsOnAFreezeOrATrackStart(string entry, string? identityCallsign)
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "COAST T0 S300", Sector44).Success);

        Assert.True(Apply(ac, entry, identityCallsign is null ? null : Sector44).Success);

        Assert.False(ac.Eram.IsCoastTrack);
        Assert.Null(ac.Eram.CoastLat);
        Assert.Null(ac.Eram.CoastPositionAt(60));
    }

    [Fact]
    public void Coast_EndsOnADrop()
    {
        AircraftState ac = Aircraft();
        Assert.True(Apply(ac, "COAST T0 S300", Sector44).Success);

        Assert.True(TrackEngine.HandleDrop(ac).Success);

        Assert.False(ac.Eram.IsCoastTrack);
        Assert.Null(ac.Track.Owner);
    }

    [Theory]
    [InlineData("COAST T0", EramEntryErrors.AlreadyTracked)]
    [InlineData("COAST /OK T0", EramEntryErrors.CofieFormat + " /OK")] // the Coast Track format has no field 60
    public void Coast_OnAnotherSectorsTrack_IsRefused(string entry, string message)
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Sector45;

        CommandResult result = Apply(ac, entry, Sector44);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.False(ac.Eram.IsCoastTrack);
        Assert.Same(Sector45, ac.Track.Owner);
    }

    [Fact]
    public void Coast_FromPastAFixBehind_DoesNotTurnBack()
    {
        // A vectored aircraft's remaining route starts at the fix nearest it, which it has already passed.
        AircraftState ac = Aircraft();
        var behind = new LatLon(37.7, -122.0);
        var next = new LatLon(38.0, -122.0);
        var after = new LatLon(38.3, -122.0);

        Assert.True(Apply(ac, "COAST T0 @37.75,-122.0 S600 R37.7,-122.0 R38.0,-122.0 R38.3,-122.0", Sector44).Success);

        Assert.Equal([next, after], ac.Eram.CoastRoute);
        Assert.True(ac.Eram.CoastPositionAt(30)!.Value.Lat > 37.75);
        Assert.NotEqual(behind, ac.Eram.CoastRoute[0]);
    }

    [Fact]
    public void Coast_FromPastTheFinalFix_HoldsTheLastLegsCourse()
    {
        AircraftState ac = Aircraft();
        ac.TrueTrack = new TrueHeading(270);

        Assert.True(Apply(ac, "COAST T0 @38.1,-122.0 S600 R37.7,-122.0 R38.0,-122.0", Sector44).Success);

        Assert.Empty(ac.Eram.CoastRoute);
        double lastLeg = GeoMath.BearingTo(new LatLon(37.7, -122.0), new LatLon(38.0, -122.0));
        Assert.Equal(lastLeg, ac.Eram.CoastTrueCourse!.Value, 6);
        Assert.True(ac.Eram.CoastPositionAt(60)!.Value.Lat > 38.1);
    }

    [Fact]
    public void Coast_WithALoneFixBehind_HoldsTheTrack()
    {
        AircraftState ac = Aircraft();
        ac.TrueTrack = new TrueHeading(0);

        Assert.True(Apply(ac, "COAST T0 @37.8,-122.0 S600 R37.7,-122.0", Sector44).Success);

        Assert.Empty(ac.Eram.CoastRoute);
        Assert.Equal(0, ac.Eram.CoastTrueCourse!.Value, 6);
    }

    [Fact]
    public void Recoast_WithNoHeadingOrRoute_KeepsTheCoastsCourse()
    {
        AircraftState ac = Aircraft();
        ac.TrueTrack = new TrueHeading(0);
        Assert.True(Apply(ac, "COAST T0 @37.5,-122 S360 H90", Sector44).Success);
        double coastCourse = ac.Eram.CoastTrueCourse!.Value;

        Assert.True(Apply(ac, "COAST T60 S300", Sector44).Success);

        Assert.Equal(coastCourse, ac.Eram.CoastTrueCourse!.Value, 6);
        Assert.Equal(300, ac.Eram.CoastSpeed);
        LatLon sixNmOut = GeoMath.ProjectPoint(new LatLon(37.5, -122), new TrueHeading(coastCourse), 6.0);
        Assert.True(GeoMath.DistanceNm(sixNmOut, new LatLon(ac.Eram.CoastLat!.Value, ac.Eram.CoastLon!.Value)) < 0.01);
    }

    [Theory]
    [InlineData("COAST", EramEntryErrors.MessageTooShort)]
    [InlineData("COAST S300", EramEntryErrors.MessageTooShort)]
    [InlineData("COAST T0 S3x0", EramEntryErrors.CofieFormat + " S3x0")]
    [InlineData("COAST T0 @north", EramEntryErrors.CofieFormat + " @north")]
    [InlineData("COAST T0 R91,0", EramEntryErrors.CofieFormat + " R91,0")]
    [InlineData("COAST T0 X1", EramEntryErrors.CofieFormat + " X1")]
    [InlineData("COAST TInfinity", EramEntryErrors.CofieFormat + " TInfinity")]
    [InlineData("COAST TNaN", EramEntryErrors.CofieFormat + " TNaN")]
    [InlineData("COAST T-1", EramEntryErrors.CofieFormat + " T-1")]
    [InlineData("COAST T0 H361", EramEntryErrors.CofieFormat + " H361")]
    public void Coast_Malformed_IsRefused(string entry, string message)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, Sector44);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.False(ac.Eram.IsCoastTrack);
        Assert.Null(ac.Track.Owner);
    }

    [Fact]
    public void Coast_WithoutAnIdentity_IsRefused()
    {
        AircraftState ac = Aircraft();

        Assert.Equal(EramEntryErrors.SessionNotActive, Apply(ac, "COAST T0", null).Message);
        Assert.False(ac.Eram.IsCoastTrack);
    }

    // CRC's CompassDirection / TurnDirection ordinals, as AircraftHoldAnnotation stores them.
    private const int North = 1;
    private const int Northeast = 2;
    private const int East = 4;
    private const int South = 6;
    private const int LeftTurns = 0;
    private const int RightTurns = 1;

    private static AircraftState AircraftHoldingAtOak()
    {
        AircraftState ac = Aircraft();
        ac.HoldAnnotation = new AircraftHoldAnnotation
        {
            Fix = "OAK",
            Direction = North,
            Turns = LeftTurns,
            LegLength = 5,
            LegLengthInNm = true,
            Efc = 1230,
        };
        return ac;
    }

    [Fact]
    public void Hm_NewHoldAtAFix_StoresTheFixEfcAndInstructions()
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, "HM OAK/1230 NE/LT/10NM", Sector44);

        Assert.True(result.Success, result.Message);
        Assert.Equal("OAK", ac.HoldAnnotation.Fix);
        Assert.Equal(1230, ac.HoldAnnotation.Efc);
        Assert.Equal(Northeast, ac.HoldAnnotation.Direction);
        Assert.Equal(LeftTurns, ac.HoldAnnotation.Turns);
        Assert.Equal(10, ac.HoldAnnotation.LegLength);
        Assert.True(ac.HoldAnnotation.LegLengthInNm);
        Assert.Null(ac.HoldAnnotation.Radial);
    }

    [Fact]
    public void Hm_NewHoldAtAFix_ReplacesTheStoredHoldAndLeavesUngivenInstructionsBlank()
    {
        AircraftState ac = AircraftHoldingAtOak();

        Assert.True(Apply(ac, "HM SFO", Sector44).Success);

        Assert.Equal("SFO", ac.HoldAnnotation.Fix);
        Assert.Null(ac.HoldAnnotation.Direction);
        Assert.Null(ac.HoldAnnotation.Turns);
        Assert.Null(ac.HoldAnnotation.LegLength);
        Assert.Null(ac.HoldAnnotation.Efc);
    }

    [Fact]
    public void Hm_PresentPosition_StoresTheAircraftPositionAsAnEramLatLong()
    {
        AircraftState ac = Aircraft();

        Assert.True(Apply(ac, "HM P/0915", Sector44).Success);

        Assert.Equal("3742N/12212W", ac.HoldAnnotation.Fix);
        Assert.Equal(915, ac.HoldAnnotation.Efc);
        Assert.Null(ac.HoldAnnotation.Direction);
    }

    [Fact]
    public void Hm_LatLongWithoutAnEfc_IsTheWholeLocation()
    {
        AircraftState ac = Aircraft();

        Assert.True(Apply(ac, "HM 3730/12200", Sector44).Success);

        Assert.Equal("3730/12200", ac.HoldAnnotation.Fix);
        Assert.Null(ac.HoldAnnotation.Efc);
    }

    [Fact]
    public void Hm_FourDigitHeadBeforeFourDigits_IsALatLongWithoutAnEfc()
    {
        AircraftState ac = Aircraft();

        Assert.True(Apply(ac, "HM 3730/1230", Sector44).Success);

        Assert.Equal("3730/1230", ac.HoldAnnotation.Fix);
        Assert.Null(ac.HoldAnnotation.Efc);
    }

    [Fact]
    public void Hm_EfcOnly_MergesIntoTheStoredHold()
    {
        AircraftState ac = AircraftHoldingAtOak();

        Assert.True(Apply(ac, "HM 1300", Sector44).Success);

        Assert.Equal("OAK", ac.HoldAnnotation.Fix);
        Assert.Equal(1300, ac.HoldAnnotation.Efc);
        Assert.Equal(North, ac.HoldAnnotation.Direction);
        Assert.Equal(5, ac.HoldAnnotation.LegLength);
    }

    [Fact]
    public void Hm_DeleteEfc_KeepsTheRestOfTheStoredHold()
    {
        AircraftState ac = AircraftHoldingAtOak();

        Assert.True(Apply(ac, "HM /*", Sector44).Success);

        Assert.Equal("OAK", ac.HoldAnnotation.Fix);
        Assert.Null(ac.HoldAnnotation.Efc);
        Assert.Equal(North, ac.HoldAnnotation.Direction);
    }

    [Fact]
    public void Hm_HoldingInstructionsAlone_ReplaceTheStoredInstructions()
    {
        AircraftState ac = AircraftHoldingAtOak();

        Assert.True(Apply(ac, "HM 093/RT/2MIN", Sector44).Success);

        Assert.Equal("OAK", ac.HoldAnnotation.Fix);
        Assert.Equal(1230, ac.HoldAnnotation.Efc);
        Assert.Equal(93, ac.HoldAnnotation.Radial);
        Assert.Equal(East, ac.HoldAnnotation.Direction);
        Assert.Equal(RightTurns, ac.HoldAnnotation.Turns);
        Assert.Equal(2, ac.HoldAnnotation.LegLength);
        Assert.False(ac.HoldAnnotation.LegLengthInNm);
    }

    [Fact]
    public void Hm_StandardLeg_StoresNoLegLength()
    {
        AircraftState ac = AircraftHoldingAtOak();

        Assert.True(Apply(ac, "HM S/RT/STD", Sector44).Success);

        Assert.Equal(South, ac.HoldAnnotation.Direction);
        Assert.Null(ac.HoldAnnotation.LegLength);
        Assert.False(ac.HoldAnnotation.LegLengthInNm);
    }

    [Fact]
    public void Qh_HoldAmend_ReplacesTheHoldWithTheNewFixAndInstructions()
    {
        AircraftState ac = AircraftHoldingAtOak();

        CommandResult result = Apply(ac, "QH SFO/0915 S/RT/STD", Sector44);

        Assert.True(result.Success, result.Message);
        Assert.Equal("SFO", ac.HoldAnnotation.Fix);
        Assert.Equal(915, ac.HoldAnnotation.Efc);
        Assert.Equal(South, ac.HoldAnnotation.Direction);
        Assert.Equal(RightTurns, ac.HoldAnnotation.Turns);
        Assert.Null(ac.HoldAnnotation.LegLength);
    }

    [Fact]
    public void Qh_EfcOnly_MergesIntoTheStoredHold()
    {
        AircraftState ac = AircraftHoldingAtOak();

        Assert.True(Apply(ac, "QH 0100", Sector44).Success);

        Assert.Equal("OAK", ac.HoldAnnotation.Fix);
        Assert.Equal(100, ac.HoldAnnotation.Efc);
        Assert.Equal(LeftTurns, ac.HoldAnnotation.Turns);
    }

    [Theory]
    [InlineData("HM C")]
    [InlineData("QH C")]
    public void HoldCancel_ClearsTheStoredHold(string entry)
    {
        AircraftState ac = AircraftHoldingAtOak();

        Assert.True(Apply(ac, entry, Sector44).Success);

        Assert.Null(ac.HoldAnnotation.Fix);
        Assert.Null(ac.HoldAnnotation.Efc);
        Assert.Null(ac.HoldAnnotation.Direction);
    }

    [Theory]
    [InlineData("HM 1300")]
    [InlineData("HM /*")]
    [InlineData("QH N/LT/STD")]
    public void HoldChange_WithNoStoredHold_IsTooShort(string entry)
    {
        AircraftState ac = Aircraft();

        CommandResult result = Apply(ac, entry, Sector44);

        Assert.False(result.Success);
        Assert.Equal(EramEntryErrors.MessageTooShort, result.Message);
        Assert.Null(ac.HoldAnnotation.Fix);
    }

    [Theory]
    [InlineData("HM", EramEntryErrors.MessageTooShort)]
    [InlineData("HM OAK N/LT/STD X", EramEntryErrors.MessageTooLong)]
    [InlineData("HM C N/LT/STD", EramEntryErrors.InvalidCombination)]
    [InlineData("HM OAK/2599", EramEntryErrors.InvalidTime)]
    [InlineData("HM 2460", EramEntryErrors.InvalidTime)]
    [InlineData("HM N/LT/XX", EramEntryErrors.CofieFormat + " N/LT/XX")]
    [InlineData("HM 045/LT/STD", EramEntryErrors.CofieFormat + " 045/LT/STD")]
    [InlineData("QH N/LT/10MIN", EramEntryErrors.CofieFormat + " N/LT/10MIN")]
    public void Hold_Malformed_IsRefused(string entry, string message)
    {
        AircraftState ac = AircraftHoldingAtOak();

        CommandResult result = Apply(ac, entry, Sector44);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Equal("OAK", ac.HoldAnnotation.Fix);
        Assert.Equal(1230, ac.HoldAnnotation.Efc);
    }
}

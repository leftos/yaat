using Xunit;
using Yaat.Sim.Commands;

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
        var ac = Aircraft();
        ac.Track.HandoffPeer = Sector45;
        ac.Track.HandoffInitiatedAt = 12;
        ac.Eram.IsFrozen = true;
        ac.Eram.FrozenLat = 37.0;
        ac.Eram.FrozenLon = -122.0;
        ac.Eram.FrozenAltitude = 110;

        var result = EramEntryEngine.Apply(ac, "TRACK", Sector44);

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
        var ac = Aircraft();
        ac.Track.Owner = Sector45;

        var result = EramEntryEngine.Apply(ac, entry, Sector44);

        Assert.Equal(taken, result.Success);
        Assert.Same(taken ? Sector44 : Sector45, ac.Track.Owner);
        if (!taken)
        {
            Assert.Equal("ALREADY TRACKED", result.Message);
        }
    }

    [Fact]
    public void Track_WithoutAnIdentity_IsRefused()
    {
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, "TRACK", null);

        Assert.False(result.Success);
        Assert.Equal("NOT ACTIVE", result.Message);
        Assert.Null(ac.Track.Owner);
    }

    [Fact]
    public void Freeze_ParksTheTrack_AndSnapshotsTheAltitude()
    {
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, "FREEZE 37.25 -121.75", null);

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
        var ac = Aircraft();

        Assert.False(EramEntryEngine.Apply(ac, entry, null).Success);
        Assert.False(ac.Eram.IsFrozen);
    }

    [Theory]
    [InlineData("QQ 110", 110, null, null, null, "QQ 110 UAL1")]
    [InlineData("QQ R110", 110, null, null, 110, "QQ R110 UAL1")]
    [InlineData("QQ L090", null, 90, null, null, "QQ L90 UAL1")]
    [InlineData("QQ P070", null, null, 70, null, "QQ P70 UAL1")]
    public void Qq_SetsTheAltitudeTier(string entry, int? interim, int? local, int? procedure, int? cera, string message)
    {
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, entry, null);

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
        var ac = Aircraft();

        EramEntryEngine.Apply(ac, "QQ 110", null);
        EramEntryEngine.Apply(ac, "QQ P070", null);

        Assert.Null(ac.Eram.InterimAltitude);
        Assert.Equal(70, ac.Eram.ProcedureAltitude);
    }

    [Fact]
    public void Qq_Bare_ClearsInterimAndProcedure_AndQqL_ClearsTheLocalOnly()
    {
        var ac = Aircraft();
        ac.Eram.InterimAltitude = 110;
        ac.Eram.ProcedureAltitude = 70;
        ac.Eram.LocalInterimAltitude = 90;

        Assert.True(EramEntryEngine.Apply(ac, "QQ", null).Success);
        Assert.Null(ac.Eram.InterimAltitude);
        Assert.Null(ac.Eram.ProcedureAltitude);
        Assert.Equal(90, ac.Eram.LocalInterimAltitude);

        Assert.True(EramEntryEngine.Apply(ac, "QQ L", null).Success);
        Assert.Null(ac.Eram.LocalInterimAltitude);
    }

    [Fact]
    public void Qq_WithNoNumericToken_IsRefused()
    {
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, "QQ ABC", null);

        Assert.False(result.Success);
        Assert.Equal("FORMAT", result.Message);
    }

    [Fact]
    public void Qr_SetsTheControllerEnteredAltitude()
    {
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, "QR 250", null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("QR 250 UAL1", result.Message);
        Assert.Equal(250, ac.Eram.ControllerEnteredAltitude);
        Assert.Null(ac.Eram.InterimAltitude);
    }

    [Theory]
    [InlineData("QR")]
    [InlineData("QR 0")]
    [InlineData("QR X")]
    public void Qr_WithoutAPositiveAltitude_IsRefused(string entry)
    {
        Assert.False(EramEntryEngine.Apply(Aircraft(), entry, null).Success);
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
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, entry, null);

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
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, entry, null);

        Assert.False(result.Success);
        Assert.Equal("FORMAT", result.Message);
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
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, entry, null);

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
        var ac = Aircraft();

        Assert.False(EramEntryEngine.Apply(ac, entry, null).Success);
        Assert.Null(ac.Eram.AssignedSpeed);
    }

    [Fact]
    public void Qs_FreeText_IsUpperCased_AndCapped()
    {
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, "QS `expect ils 28r", null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("EXPECT ILS 28R", ac.Eram.FreeText);
        Assert.Equal("QS EXPECT ILS 28R UAL1", result.Message);

        var longText = new string('X', EramEntryEngine.FreeTextMaxLength + 5);
        Assert.True(EramEntryEngine.Apply(ac, $"QS `{longText}", null).Success);
        Assert.Equal(EramEntryEngine.FreeTextMaxLength, ac.Eram.FreeText!.Length);
    }

    [Fact]
    public void Qs_EmptyFreeText_IsRefused()
    {
        var ac = Aircraft();

        var result = EramEntryEngine.Apply(ac, "QS `", null);

        Assert.False(result.Success);
        Assert.Equal("FORMAT", result.Message);
    }

    [Theory]
    [InlineData("QS *", null, null, null)]
    [InlineData("QS */", null, "250", "TXT")]
    [InlineData("QS /*", "H270", null, "TXT")]
    public void Qs_DeleteForms_ClearTheNamedFields(string entry, string? heading, string? speed, string? text)
    {
        var ac = Aircraft();
        ac.Eram.AssignedHeading = "H270";
        ac.Eram.AssignedSpeed = "250";
        ac.Eram.FreeText = "TXT";

        var result = EramEntryEngine.Apply(ac, entry, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(heading, ac.Eram.AssignedHeading);
        Assert.Equal(speed, ac.Eram.AssignedSpeed);
        Assert.Equal(text, ac.Eram.FreeText);
    }

    [Fact]
    public void Lf_SetsTheGroupLabel_AndBareLfClearsIt()
    {
        var ac = Aircraft();

        Assert.True(EramEntryEngine.Apply(ac, "LF ABC", null).Success);
        Assert.Equal("ABC", ac.Eram.CrrGroupLabel);

        Assert.True(EramEntryEngine.Apply(ac, "LF", null).Success);
        Assert.Null(ac.Eram.CrrGroupLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("QZ 350")]
    [InlineData("HELLO")]
    public void UnknownEntry_IsRefused(string entry)
    {
        Assert.False(EramEntryEngine.Apply(Aircraft(), entry, Sector44).Success);
    }
}

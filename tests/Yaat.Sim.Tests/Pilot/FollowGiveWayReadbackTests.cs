using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Pilot readbacks for <c>FOLLOW</c>, <c>FOLLOWG</c> and <c>GIVEWAY</c>. All name the other aircraft only as
/// "the traffic" in the spoken and solo-terminal forms — a pilot identifies traffic by the
/// controller's position call, never by callsign (docs/pilot-phraseology.md) — so the target
/// callsign survives only in <see cref="PilotSpeechText.RpoTerminal"/> as an instructor aid.
///
/// <para>An airborne <c>FOLLOW</c> — and its RPO-only <c>FOLLOWF</c>, which parses to the same command — reads
/// back the same "follow the traffic", built directly beside the ground forms in
/// <c>PilotResponder.VerbalizeForReadback</c> (7110.65 §7-6-7.a "FOLLOW (description)"; also §3-8-1, tower
/// sequencing).</para>
///
/// "FOLLOW (traffic) (restrictions as necessary)" and "BEHIND (traffic)" are §3-7-2.a taxi elements.
/// The rules that map their spoken forms to <c>FOLLOWG</c> / <c>GIVEWAY</c> remain for speech
/// recognition; the readback is built directly in <c>PilotResponder.VerbalizeForReadback</c>.
/// </summary>
public sealed class FollowGiveWayReadbackTests
{
    private readonly ITestOutputHelper _output;

    public FollowGiveWayReadbackTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Aircraft(string callsign, string type) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = new LatLon(37.6207, -122.3827),
            TrueHeading = new TrueHeading(270),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };

    private static AircraftState Airborne(string callsign, string type) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = new LatLon(37.6207, -122.3827),
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            Altitude = 3000,
            IndicatedAirspeed = 110,
            IsOnGround = false,
        };

    private static PilotSpeechText Readback(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        PilotSpeechText? result = PilotResponder.BuildReadback(parsed.Value!, aircraft, ActiveRunways.Empty);
        Assert.NotNull(result);
        return result;
    }

    [Fact]
    public void FollowGround_TargetCallsign_IsRpoDiagnosticOnly()
    {
        PilotSpeechText result = Readback(Aircraft("N12345", "C172"), "FOLLOWG N2468K");

        Assert.Equal("follow the traffic", result.Terminal);
        Assert.Equal("follow the traffic, november one two three four five.", result.Tts);
        Assert.Equal("follow N2468K", result.RpoTerminal);
    }

    [Fact]
    public void FollowGround_ThenCross_KeepsBothClausesInEveryForm()
    {
        PilotSpeechText result = Readback(Aircraft("N12345", "C172"), "FOLLOWG N2468K; CROSS 28R");

        Assert.Equal("follow the traffic, then cross runway 28R", result.Terminal);
        Assert.StartsWith("follow the traffic, then cross runway two eight right, ", result.Tts, StringComparison.Ordinal);
        Assert.Equal("follow N2468K, then cross runway 28R", result.RpoTerminal);
    }

    [Fact]
    public void GiveWay_TargetCallsign_IsRpoDiagnosticOnly()
    {
        PilotSpeechText result = Readback(Aircraft("SWA123", "B738"), "GIVEWAY UAL456");

        Assert.Equal("behind the traffic", result.Terminal);
        Assert.Equal("behind the traffic, southwest one twenty three.", result.Tts);
        Assert.Equal("behind UAL456", result.RpoTerminal);
    }

    [Fact]
    public void TaxiWithAppendedGiveWay_VoicesBothClauses()
    {
        PilotSpeechText result = Readback(Aircraft("SWA123", "B738"), "TAXI A A1 1R GIVEWAY UAL456");

        // The TAXI clause keeps the existing terminal render ("taxi to runway 1R via A A1"); by the
        // parser the appended GIVEWAY is a second command sharing the one block, so it joins with ", ".
        Assert.Equal("taxi to runway 1R via A A1, behind the traffic", result.Terminal);
        Assert.EndsWith("behind the traffic, southwest one twenty three.", result.Tts, StringComparison.Ordinal);
        Assert.Equal("taxi to runway 1R via A A1, behind UAL456", result.RpoTerminal);
    }

    [Fact]
    public void GiveWayAsConditionPrefix_OpensTheReadbackWithTheBehindLead()
    {
        PilotSpeechText result = Readback(Aircraft("SWA123", "B738"), "GIVEWAY UAL456 TAXI A A1 1R");
        _output.WriteLine($"terminal='{result.Terminal}' tts='{result.Tts}' rpo='{result.RpoTerminal}'");

        Assert.Equal("behind the traffic, taxi to runway 1R via A A1", result.Terminal);
        Assert.Equal("behind the traffic, taxi to runway one right via alpha, alpha one, southwest one twenty three.", result.Tts);
        Assert.Equal("behind UAL456, taxi to runway 1R via A A1", result.RpoTerminal);
    }

    [Fact]
    public void CrossAlone_HasNoRpoDiagnostic()
    {
        // Guards the equal-means-null rule: a readback with no diagnostic leaves RpoTerminal null, so
        // the RPO branch falls back to Terminal rather than duplicating it.
        PilotSpeechText result = Readback(Aircraft("N12345", "C172"), "CROSS 28R");

        Assert.Null(result.RpoTerminal);
    }

    [Fact]
    public void FollowAirborne_TargetCallsign_IsRpoDiagnosticOnly()
    {
        PilotSpeechText result = Readback(Airborne("N12345", "C172"), "FOLLOW N2468K");

        Assert.Equal("follow the traffic", result.Terminal);
        Assert.Equal("follow the traffic, november one two three four five.", result.Tts);
        Assert.Equal("follow N2468K", result.RpoTerminal);
        Assert.DoesNotContain("N2468K", result.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("N2468K", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowForcedAirborne_TargetCallsign_IsRpoDiagnosticOnly()
    {
        // FOLLOWF (force) is the same FollowCommand with Force set, so it shares the airborne readback.
        PilotSpeechText result = Readback(Airborne("N12345", "C172"), "FOLLOWF N2468K");

        Assert.Equal("follow the traffic", result.Terminal);
        Assert.Equal("follow the traffic, november one two three four five.", result.Tts);
        Assert.Equal("follow N2468K", result.RpoTerminal);
        Assert.DoesNotContain("N2468K", result.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("N2468K", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowAirborne_BareFollow_KeepsTheTrafficInEveryForm()
    {
        // A bare FOLLOW carries no callsign (it defaults to the traffic last reported in sight), so there is no
        // diagnostic to hide: the RPO body matches the terminal one and the equal-means-null rule drops it.
        PilotSpeechText result = Readback(Airborne("N12345", "C172"), "FOLLOW");

        Assert.Equal("follow the traffic", result.Terminal);
        Assert.Equal("follow the traffic, november one two three four five.", result.Tts);
        Assert.Null(result.RpoTerminal);
        Assert.Equal("follow the traffic", result.TerminalForRpo);
    }

    [Fact]
    public void ConditionLead_SurvivesInTheRpoBody()
    {
        // Exercises the condition-lead plumbing, not a realistic ground clearance — 7110.65 has no
        // fix-conditioned taxi element, so no real controller issues "at SUNOL, follow the traffic".
        PilotSpeechText result = Readback(Aircraft("N12345", "C172"), "AT SUNOL FOLLOWG N2468K");

        Assert.Equal("at SUNOL, follow the traffic", result.Terminal);
        Assert.Equal("at SUNOL, follow N2468K", result.RpoTerminal);
    }
}

using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Tests for the M10.1.4 Contact (CT) and FrequencyChangeApproved (FCA) commands. These exercise
/// the live-config path against the committed ZOA snapshot so frequency lookup, callsign
/// disambiguation, and TCP fallback all run against real position data.
/// </summary>
public class ContactCommandHandlerTests
{
    private static AircraftState MakeAircraft(string callsign = "N123AB") => new() { Callsign = callsign, AircraftType = "C172" };

    private static DispatchContext MakeCtx(ArtccConfigRoot? config, bool soloTrainingMode = true, bool rpoShowPilotSpeech = false) =>
        TestDispatch.Context(new Random(0), soloTrainingMode: soloTrainingMode, rpoShowPilotSpeech: rpoShowPilotSpeech, artccConfig: config);

    private static PilotTransmission SingleTransmission(AircraftState ac) => Assert.Single(ac.PendingPilotTransmissions);

    // --- Explicit target: position callsign ---

    [Fact]
    public void Contact_PositionCallsign_ResolvesAndEmitsReadback()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return; // snapshot absent — skip silently
        }

        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        Assert.True(result.Success);
        var transmission = SingleTransmission(ac);
        Assert.Empty(ac.PendingNotifications);
        Assert.StartsWith("Oakland Tower on one two seven point two,", transmission.SpeechText);
        Assert.Contains(", november one two three alpha bravo, so long.", transmission.SpeechText);
    }

    [Fact]
    public void Contact_PositionCallsign_DisambiguatesGndVsTwrOnSharedTcp()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var ac1 = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_GND"), ac1, MakeCtx(config));
        var groundReadback = SingleTransmission(ac1).SpeechText;

        var ac2 = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac2, MakeCtx(config));
        var towerReadback = SingleTransmission(ac2).SpeechText;

        Assert.Contains("Oakland Ground", groundReadback);
        Assert.Contains("Oakland Tower", towerReadback);
        Assert.NotEqual(groundReadback, towerReadback);
    }

    [Fact]
    public void Contact_NorCalApproach_UsesRadioNameAndCompactsFrequency()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        // OAK_G_APP at 125.35 MHz has RadioName "NorCal Approach" — the bug-bundle scenario
        // showed this rendering as just lowercase "approach" with a spoken-digit frequency.
        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleContact(new ContactCommand("OAK_G_APP"), ac, MakeCtx(config));

        Assert.True(result.Success);
        var transmission = SingleTransmission(ac);
        Assert.StartsWith("NorCal Approach on one two five point three five,", transmission.SpeechText);
    }

    // --- Explicit target: frequency ---

    [Fact]
    public void Contact_Frequency_ResolvesByMhz()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var oakTwr = config.FindPositionByCallsign("OAK_TWR");
        Assert.NotNull(oakTwr);
        var freqMhz = oakTwr.Frequency / 1_000_000.0;

        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleContact(
            new ContactCommand(freqMhz.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
            ac,
            MakeCtx(config)
        );

        Assert.True(result.Success);
        var transmission = SingleTransmission(ac);
        Assert.Contains("Oakland Tower on ", transmission.SpeechText);
    }

    // --- Explicit target: TCP code ---

    [Fact]
    public void Contact_TcpCode_UnambiguousResolvesToPosition()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        // Find a TCP where exactly one position links to it, so the resolution is unambiguous.
        // Most consolidated TWR/GND TCPs have two — pick a sector that doesn't.
        var unambiguous = FindUnambiguousTcpCode(config);
        if (unambiguous is null)
        {
            return; // unusual snapshot — every TCP shared
        }

        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleContact(new ContactCommand(unambiguous), ac, MakeCtx(config));

        Assert.True(result.Success);
        SingleTransmission(ac);
    }

    [Fact]
    public void Contact_TcpCode_AmbiguousRejectsWithCandidates()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        // Find a TCP where two or more positions link to it (real-world example: OAK_TWR + OAK_GND
        // on a consolidated STARS scope). The handler must refuse to silently pick one.
        var (ambiguousTcp, candidates) = FindAmbiguousTcpCode(config);
        if (ambiguousTcp is null)
        {
            return; // unusual snapshot — no shared TCPs
        }

        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleContact(new ContactCommand(ambiguousTcp), ac, MakeCtx(config));

        Assert.False(result.Success);
        Assert.Contains("ambiguous TCP", result.Message ?? "");
        // Candidate callsigns should be listed so the controller knows what to type.
        foreach (var c in candidates)
        {
            Assert.Contains(c.Callsign, result.Message ?? "");
        }
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    private static string? FindUnambiguousTcpCode(ArtccConfigRoot config)
    {
        foreach (var facilityId in EnumerateFacilityIds(config))
        {
            var facility = config.FindFacility(facilityId);
            if (facility?.StarsConfiguration is null)
            {
                continue;
            }
            foreach (var tcp in facility.StarsConfiguration.Tcps)
            {
                var code = $"{tcp.Subset}{tcp.SectorId}";
                if (config.FindPositionsByTcpCodeAnyFacility(code).Count == 1)
                {
                    return code;
                }
            }
        }
        return null;
    }

    private static (string? Code, IReadOnlyList<PositionConfig> Candidates) FindAmbiguousTcpCode(ArtccConfigRoot config)
    {
        foreach (var facilityId in EnumerateFacilityIds(config))
        {
            var facility = config.FindFacility(facilityId);
            if (facility?.StarsConfiguration is null)
            {
                continue;
            }
            foreach (var tcp in facility.StarsConfiguration.Tcps)
            {
                var code = $"{tcp.Subset}{tcp.SectorId}";
                var matches = config.FindPositionsByTcpCodeAnyFacility(code);
                if (matches.Count >= 2)
                {
                    return (code, matches);
                }
            }
        }
        return (null, Array.Empty<PositionConfig>());
    }

    private static IEnumerable<string> EnumerateFacilityIds(ArtccConfigRoot config)
    {
        var stack = new Stack<FacilityConfig>();
        stack.Push(config.Facility);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            yield return f.Id;
            foreach (var child in f.ChildFacilities)
            {
                stack.Push(child);
            }
        }
    }

    // --- Explicit target: unknown ---

    [Fact]
    public void Contact_UnknownPosition_Rejects()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleContact(new ContactCommand("XYZ_BOGUS"), ac, MakeCtx(config));

        Assert.False(result.Success);
        Assert.Contains("unknown position", result.Message ?? "");
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    // --- Auto-resolve (no arg) ---

    [Fact]
    public void Contact_NoArg_NoHandoff_Rejects()
    {
        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(null));

        Assert.False(result.Success);
        Assert.Contains("no handoff target", result.Message ?? "");
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void Contact_NoArg_HandoffPeerSet_UsesPeer()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var ac = MakeAircraft();
        ac.Track.HandoffPeer = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");

        var result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(config));

        Assert.True(result.Success);
        var transmission = SingleTransmission(ac);
        Assert.Contains("Oakland Tower on ", transmission.SpeechText);
    }

    [Fact]
    public void Contact_NoArg_HandoffAccepted_UsesOwner()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var ac = MakeAircraft();
        ac.Track.Owner = TrackOwner.CreateStars("OAK_GND", "OAK", 3, "O");
        ac.Track.HandoffAccepted = true;

        var result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(config));

        Assert.True(result.Success);
        var transmission = SingleTransmission(ac);
        Assert.Contains("Oakland Ground on ", transmission.SpeechText);
    }

    [Fact]
    public void Contact_NoArg_OwnerSetButNotAccepted_Rejects()
    {
        var ac = MakeAircraft();
        ac.Track.Owner = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        // HandoffAccepted is false by default — controller still owns the track.
        var result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(null));

        Assert.False(result.Success);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    // --- Idempotence ---

    [Fact]
    public void Contact_TwiceInARow_BothEmitReadback()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var ac = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
    }

    // --- FCA ---

    [Fact]
    public void Fca_AlwaysSucceeds_NoStateRequired()
    {
        var ac = MakeAircraft();
        var result = ContactCommandHandler.HandleFrequencyChangeApproved(ac, MakeCtx(null));

        Assert.True(result.Success);
        var transmission = SingleTransmission(ac);
        Assert.Equal("november one two three alpha bravo, good day.", transmission.SpeechText);
    }

    // --- Routing: solo / RPO+flag / RPO ---

    [Fact]
    public void Contact_RpoShowPilotSpeech_RoutesToPendingPilotSpeech()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var ac = MakeAircraft();
        var ctx = MakeCtx(config, soloTrainingMode: false, rpoShowPilotSpeech: true);
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Single(ac.PendingPilotSpeech);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Contact_RpoNoFlag_RoutesToPendingWarnings()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var ac = MakeAircraft();
        var ctx = MakeCtx(config, soloTrainingMode: false, rpoShowPilotSpeech: false);
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Single(ac.PendingWarnings);
    }

    // --- Frequency formatting per FAA 7110.65 §2-4-16: spoken digit-by-digit for TTS,
    //     compacted to numeric form for the terminal display. ---

    [Fact]
    public void Contact_PositionFrequency_DelayedSayKeepsSpokenForm()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var oakTwr = config.FindPositionByCallsign("OAK_TWR");
        Assert.NotNull(oakTwr);
        var freqMhz = oakTwr.Frequency / 1_000_000.0;
        var expectedSpoken = PhraseologyVerbalizer.FrequencyToWords(freqMhz);

        var ac = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        Assert.Empty(ac.PendingNotifications);
        Assert.Contains($"Oakland Tower on {expectedSpoken}", SingleTransmission(ac).SpeechText);
    }

    [Fact]
    public void Contact_PositionFrequency_PilotSpeechKeepsSpokenForm()
    {
        var config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        var oakTwr = config.FindPositionByCallsign("OAK_TWR");
        Assert.NotNull(oakTwr);
        var freqMhz = oakTwr.Frequency / 1_000_000.0;
        var expectedSpoken = PhraseologyVerbalizer.FrequencyToWords(freqMhz);

        var ac = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        // PendingPilotTransmissions feed TTS — must keep the digit-by-digit spoken form.
        var transmission = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains($"Oakland Tower on {expectedSpoken}", transmission.SpeechText);
    }
}

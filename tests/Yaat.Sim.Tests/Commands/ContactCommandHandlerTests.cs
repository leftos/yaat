using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

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
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return; // snapshot absent — skip silently
        }

        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        Assert.True(result.Success);
        PilotTransmission transmission = SingleTransmission(ac);
        Assert.Empty(ac.PendingNotifications);
        Assert.StartsWith("Oakland Tower on one two seven point two,", transmission.SpeechText);
        Assert.Contains(", november one two three alpha bravo, so long.", transmission.SpeechText);
    }

    [Fact]
    public void Contact_PositionCallsign_DisambiguatesGndVsTwrOnSharedTcp()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac1 = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_GND"), ac1, MakeCtx(config));
        string groundReadback = SingleTransmission(ac1).SpeechText;

        AircraftState ac2 = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac2, MakeCtx(config));
        string towerReadback = SingleTransmission(ac2).SpeechText;

        Assert.Contains("Oakland Ground", groundReadback);
        Assert.Contains("Oakland Tower", towerReadback);
        Assert.NotEqual(groundReadback, towerReadback);
    }

    [Fact]
    public void Contact_NorCalApproach_UsesRadioNameAndCompactsFrequency()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        // OAK_G_APP at 125.35 MHz has RadioName "NorCal Approach" — the bug-bundle scenario
        // showed this rendering as just lowercase "approach" with a spoken-digit frequency.
        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand("OAK_G_APP"), ac, MakeCtx(config));

        Assert.True(result.Success);
        PilotTransmission transmission = SingleTransmission(ac);
        Assert.StartsWith("NorCal Approach on one two five point three five,", transmission.SpeechText);
    }

    // --- Explicit target: frequency ---

    [Fact]
    public void Contact_Frequency_ResolvesByMhz()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        PositionConfig? oakTwr = config.FindPositionByCallsign("OAK_TWR");
        Assert.NotNull(oakTwr);
        double freqMhz = oakTwr.Frequency / 1_000_000.0;

        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleContact(
            new ContactCommand(freqMhz.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
            ac,
            MakeCtx(config)
        );

        Assert.True(result.Success);
        PilotTransmission transmission = SingleTransmission(ac);
        Assert.Contains("Oakland Tower on ", transmission.SpeechText);
    }

    // --- Explicit target: TCP code ---

    [Fact]
    public void Contact_TcpCode_UnambiguousResolvesToPosition()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        // Find a TCP where exactly one position links to it, so the resolution is unambiguous.
        // Most consolidated TWR/GND TCPs have two — pick a sector that doesn't.
        string? unambiguous = FindUnambiguousTcpCode(config);
        if (unambiguous is null)
        {
            return; // unusual snapshot — every TCP shared
        }

        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand(unambiguous), ac, MakeCtx(config));

        Assert.True(result.Success);
        SingleTransmission(ac);
    }

    [Fact]
    public void Contact_TcpCode_AmbiguousRejectsWithCandidates()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        // Find a TCP where two or more positions link to it (real-world example: OAK_TWR + OAK_GND
        // on a consolidated STARS scope). The handler must refuse to silently pick one.
        (string? ambiguousTcp, IReadOnlyList<PositionConfig>? candidates) = FindAmbiguousTcpCode(config);
        if (ambiguousTcp is null)
        {
            return; // unusual snapshot — no shared TCPs
        }

        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand(ambiguousTcp), ac, MakeCtx(config));

        Assert.False(result.Success);
        Assert.Contains("ambiguous TCP", result.Message ?? "");
        // Candidate callsigns should be listed so the controller knows what to type.
        foreach (PositionConfig c in candidates)
        {
            Assert.Contains(c.Callsign, result.Message ?? "");
        }
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    private static string? FindUnambiguousTcpCode(ArtccConfigRoot config)
    {
        foreach (string facilityId in EnumerateFacilityIds(config))
        {
            FacilityConfig? facility = config.FindFacility(facilityId);
            if (facility?.StarsConfiguration is null)
            {
                continue;
            }
            foreach (TcpConfig tcp in facility.StarsConfiguration.Tcps)
            {
                string code = $"{tcp.Subset}{tcp.SectorId}";
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
        foreach (string facilityId in EnumerateFacilityIds(config))
        {
            FacilityConfig? facility = config.FindFacility(facilityId);
            if (facility?.StarsConfiguration is null)
            {
                continue;
            }
            foreach (TcpConfig tcp in facility.StarsConfiguration.Tcps)
            {
                string code = $"{tcp.Subset}{tcp.SectorId}";
                IReadOnlyList<PositionConfig> matches = config.FindPositionsByTcpCodeAnyFacility(code);
                if (matches.Count >= 2)
                {
                    return (code, matches);
                }
            }
        }
        return (null, []);
    }

    private static IEnumerable<string> EnumerateFacilityIds(ArtccConfigRoot config)
    {
        var stack = new Stack<FacilityConfig>();
        stack.Push(config.Facility);
        while (stack.Count > 0)
        {
            FacilityConfig f = stack.Pop();
            yield return f.Id;
            foreach (FacilityConfig child in f.ChildFacilities)
            {
                stack.Push(child);
            }
        }
    }

    // --- Explicit target: unknown ---

    [Fact]
    public void Contact_UnknownPosition_Rejects()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand("XYZ_BOGUS"), ac, MakeCtx(config));

        Assert.False(result.Success);
        Assert.Contains("unknown position", result.Message ?? "");
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    // --- Auto-resolve (no arg) ---

    [Fact]
    public void Contact_NoArg_NoHandoff_Rejects()
    {
        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(null));

        Assert.False(result.Success);
        Assert.Contains("no handoff target", result.Message ?? "");
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void Contact_NoArg_HandoffPeerSet_UsesPeer()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac = MakeAircraft();
        ac.Track.HandoffPeer = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");

        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(config));

        Assert.True(result.Success);
        PilotTransmission transmission = SingleTransmission(ac);
        Assert.Contains("Oakland Tower on ", transmission.SpeechText);
    }

    [Fact]
    public void Contact_NoArg_HandoffAccepted_UsesOwner()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac = MakeAircraft();
        ac.Track.Owner = TrackOwner.CreateStars("OAK_GND", "OAK", 3, "O");
        ac.Track.HandoffAccepted = true;

        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(config));

        Assert.True(result.Success);
        PilotTransmission transmission = SingleTransmission(ac);
        Assert.Contains("Oakland Ground on ", transmission.SpeechText);
    }

    [Fact]
    public void Contact_NoArg_OwnerSetButNotAccepted_Rejects()
    {
        AircraftState ac = MakeAircraft();
        ac.Track.Owner = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        // HandoffAccepted is false by default — controller still owns the track.
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand(null), ac, MakeCtx(null));

        Assert.False(result.Success);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    // --- Idempotence ---

    [Fact]
    public void Contact_TwiceInARow_BothEmitReadback()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        Assert.Equal(2, ac.PendingPilotTransmissions.Count);
    }

    // --- FCA ---

    [Fact]
    public void Fca_AlwaysSucceeds_NoStateRequired()
    {
        AircraftState ac = MakeAircraft();
        CommandResult result = ContactCommandHandler.HandleFrequencyChangeApproved(ac, MakeCtx(null));

        Assert.True(result.Success);
        PilotTransmission transmission = SingleTransmission(ac);
        Assert.Equal("november one two three alpha bravo, good day.", transmission.SpeechText);
    }

    // --- Routing: solo / RPO+flag / RPO ---

    [Fact]
    public void Contact_RpoShowPilotSpeech_RoutesToPendingPilotSpeech()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac = MakeAircraft();
        DispatchContext ctx = MakeCtx(config, soloTrainingMode: false, rpoShowPilotSpeech: true);
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, ctx);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Single(ac.PendingPilotSpeech);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Contact_RpoNoFlag_RoutesToPendingWarnings()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac = MakeAircraft();
        DispatchContext ctx = MakeCtx(config, soloTrainingMode: false, rpoShowPilotSpeech: false);
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
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        PositionConfig? oakTwr = config.FindPositionByCallsign("OAK_TWR");
        Assert.NotNull(oakTwr);
        double freqMhz = oakTwr.Frequency / 1_000_000.0;
        string expectedSpoken = PhraseologyVerbalizer.FrequencyToWords(freqMhz);

        AircraftState ac = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        Assert.Empty(ac.PendingNotifications);
        Assert.Contains($"Oakland Tower on {expectedSpoken}", SingleTransmission(ac).SpeechText);
    }

    // --- Origin: an AI position's (or a preset's) CT is not the student's handoff ---

    [Fact]
    public void Contact_AiOrigin_LeavesStudentFrequencyAndCompletionUntouched()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        // AI Ground hands a taxiing departure to tower: the pilot is joining the (possibly student) tower frequency,
        // not leaving the student's, and nothing has been "handed off" for the session report.
        AircraftState ac = MakeAircraft();
        DispatchContext ctx = TestDispatch.Context(
            new Random(0),
            soloTrainingMode: true,
            artccConfig: config,
            scenarioElapsedSeconds: 42,
            isScenarioScripted: true
        );
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("Oakland Tower", SingleTransmission(ac).SpeechText);
        Assert.False(ac.HasLeftStudentFrequency);
        Assert.Equal(CompletionReason.Active, ac.CompletionReason);
        Assert.Null(ac.CompletedAtSeconds);
        Assert.Null(ac.CompletionDetail);
    }

    [Fact]
    public void Contact_HumanOrigin_StillStampsHandoffAndLeavesStudentFrequency()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        AircraftState ac = MakeAircraft();
        DispatchContext ctx = TestDispatch.Context(
            new Random(0),
            soloTrainingMode: true,
            artccConfig: config,
            scenarioElapsedSeconds: 42,
            isScenarioScripted: false
        );
        CommandResult result = ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, ctx);

        Assert.True(result.Success);
        Assert.True(ac.HasLeftStudentFrequency);
        Assert.Equal(CompletionReason.HandedOff, ac.CompletionReason);
        Assert.Equal(42, ac.CompletedAtSeconds);
        Assert.Equal("OAK_TWR", ac.CompletionDetail);
    }

    [Fact]
    public void FrequencyChangeApproved_AiOrigin_LeavesStudentFrequencyAndCompletionUntouched()
    {
        AircraftState ac = MakeAircraft();
        DispatchContext ctx = TestDispatch.Context(new Random(0), soloTrainingMode: true, scenarioElapsedSeconds: 42, isScenarioScripted: true);
        CommandResult result = ContactCommandHandler.HandleFrequencyChangeApproved(ac, ctx);

        Assert.True(result.Success);
        Assert.False(ac.HasLeftStudentFrequency);
        Assert.Equal(CompletionReason.Active, ac.CompletionReason);
    }

    [Fact]
    public void Contact_PositionFrequency_PilotSpeechKeepsSpokenForm()
    {
        ArtccConfigRoot? config = TestArtccConfig.LoadZoa();
        if (config is null)
        {
            return;
        }

        PositionConfig? oakTwr = config.FindPositionByCallsign("OAK_TWR");
        Assert.NotNull(oakTwr);
        double freqMhz = oakTwr.Frequency / 1_000_000.0;
        string expectedSpoken = PhraseologyVerbalizer.FrequencyToWords(freqMhz);

        AircraftState ac = MakeAircraft();
        ContactCommandHandler.HandleContact(new ContactCommand("OAK_TWR"), ac, MakeCtx(config));

        // PendingPilotTransmissions feed TTS — must keep the digit-by-digit spoken form.
        PilotTransmission transmission = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains($"Oakland Tower on {expectedSpoken}", transmission.SpeechText);
    }
}

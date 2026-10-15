using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Logging;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Oracle;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Logging;

/// <summary>
/// A bug report bundle's recording names people in the initials slot of its commands, chat and terminal lines and in its
/// bookmarks' creators. <see cref="RecordingPseudonymizer"/> gives each listed person the server log's pseudonym and
/// everyone else <c>??</c>, keeps the system senders, and must not change what the recording replays to.
/// </summary>
public class RecordingPseudonymizerTests
{
    // A0 = Jane Doe; A1 = Pat Smith, whose initials CM are also the climb-and-maintain verb.
    private static readonly IReadOnlyList<LogParticipant> Participants =
    [
        new LogParticipant("1826127", "Jane Doe", "JD"),
        new LogParticipant("7001234", "Pat Smith", "CM"),
    ];

    private static readonly DateTime At = new(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);

    private static RecordedTerminalEntry Line(string initials, string kind, string callsign, string message) =>
        new(10, At, initials, kind, callsign, message);

    private static PseudonymizedRecording Pseudonymize(IReadOnlyList<RecordedAction> actions, IReadOnlyList<RecordedTerminalEntry> terminalLog) =>
        RecordingPseudonymizer.Pseudonymize(actions, terminalLog, Participants);

    [Fact]
    public void RecordingPseudonymizer_ReplacesActionAndTerminalInitials_KeepsAiCrcYaatAndEmpty()
    {
        var setting = new RecordedSettingChange(0, "CommandRunDelay", "2");
        List<RecordedAction> actions =
        [
            setting,
            new RecordedCommand(1, "UAL238", "CM 050", "CM", "conn-1"),
            new RecordedCommand(2, "UAL238", "FH 270", "AI", "ai"),
            new RecordedCommand(3, "N123AB", "FP B738/L KSFO KOAK", "CRC", "crc-1"),
            new RecordedCommand(4, "SKW12", "FH 090", "YAAT", "yaat"),
            new RecordedCommand(5, "SKW12", "FH 100", "", "conn-2"),
            new RecordedChat(6, "JD", "handing off to Pat Smith"),
            new RecordedChat(7, "SECTOR 12", "UAL238 frequency change approved"),
        ];
        List<RecordedTerminalEntry> terminalLog =
        [
            Line("CM", "Command", "UAL238", "CM 050"),
            Line("JD (override)", "Command", "UAL238", "FH 270"),
            Line("AI", "Command", "UAL238", "FH 270"),
            Line("CRC", "Command", "N123AB", "FP B738/L KSFO KOAK"),
            Line("YAAT", "System", "N123AB", "spawned"),
            Line("", "System", "", "JD assigned UAL238, SKW12 to CM"),
            Line("", "System", "", "CM unassigned UAL238 (available to all)"),
            Line("[CRC] 2W", "Command", "UAL238", "FP B738/L KSFO KOAK"),
            Line("SIGWX", "Chat", "", "KOAK TS >> MOV E 10KT"),
            Line("OAK_TWR", "Chat", "UAL238", "say altitude"),
            Line("OAK_TWR (Jane Doe)", "Chat", "UAL238", "say altitude"),
            Line("Pat Smith", "Chat", "UAL238", "say altitude"),
        ];

        PseudonymizedRecording result = Pseudonymize(actions, terminalLog);

        Assert.Same(setting, result.Actions[0]);
        Assert.Equal(["A1", "AI", "CRC", "YAAT", ""], result.Actions.OfType<RecordedCommand>().Select(c => c.Initials));
        // The command text replays, so it is never rewritten.
        Assert.Equal("CM 050", result.Actions.OfType<RecordedCommand>().First().Command);
        Assert.Equal(
            [new RecordedChat(6, "A0", "handing off to A1"), new RecordedChat(7, "SECTOR 12", "UAL238 frequency change approved")],
            result.Actions.OfType<RecordedChat>()
        );
        Assert.Equal(
            [
                Line("A1", "Command", "UAL238", "CM 050"),
                Line("A0 (override)", "Command", "UAL238", "FH 270"),
                Line("AI", "Command", "UAL238", "FH 270"),
                Line("CRC", "Command", "N123AB", "FP B738/L KSFO KOAK"),
                Line("YAAT", "System", "N123AB", "spawned"),
                Line("", "System", "", "A0 assigned UAL238, SKW12 to A1"),
                Line("", "System", "", "A1 unassigned UAL238 (available to all)"),
                Line("[CRC] 2W", "Command", "UAL238", "FP B738/L KSFO KOAK"),
                Line("SIGWX", "Chat", "", "KOAK TS >> MOV E 10KT"),
                Line("OAK_TWR", "Chat", "UAL238", "say altitude"),
                Line("OAK_TWR (A0)", "Chat", "UAL238", "say altitude"),
                Line("A1", "Chat", "UAL238", "say altitude"),
            ],
            result.TerminalLog
        );
    }

    [Fact]
    public void RecordingPseudonymizer_UnknownInitials_BecomeQuestionMarks()
    {
        List<RecordedAction> actions = [new RecordedCommand(1, "UAL238", "FH 270", "XY", "conn-1"), new RecordedChat(2, "B2", "back in 5")];
        List<RecordedTerminalEntry> terminalLog =
        [
            Line("XY (override)", "Command", "UAL238", "FH 270"),
            // A pseudonym-shaped token is kept only when it was issued; B2 was not.
            Line("B2", "Chat", "", "back in 5"),
            Line("OAK_TWR (Zed Outsider)", "Chat", "UAL238", "say altitude"),
            Line("Zed Outsider", "Chat", "UAL238", "say altitude"),
            Line("", "System", "", "XY assigned UAL238 to JD"),
            Line("", "System", "", "JD assigned UAL238 to XY"),
        ];

        PseudonymizedRecording result = Pseudonymize(actions, terminalLog);

        Assert.Equal("??", Assert.IsType<RecordedCommand>(result.Actions[0]).Initials);
        Assert.Equal("??", Assert.IsType<RecordedChat>(result.Actions[1]).Initials);
        Assert.Equal(["?? (override)", "??", "OAK_TWR (??)", "??", "", ""], result.TerminalLog.Select(e => e.Initials));
        Assert.Equal("?? assigned UAL238 to A0", result.TerminalLog[4].Message);
        Assert.Equal("A0 assigned UAL238 to ??", result.TerminalLog[5].Message);
    }

    // yaat-server Simulation/RoomEngine.cs:1170: $"Assigned to {ownerInitials}. Prefix with ** to override."
    [Theory]
    [InlineData("Assigned to CM. Prefix with ** to override.", "Assigned to A1. Prefix with ** to override.")]
    [InlineData("Assigned to JD. Prefix with ** to override.", "Assigned to A0. Prefix with ** to override.")]
    [InlineData("Assigned to XY. Prefix with ** to override.", "Assigned to ??. Prefix with ** to override.")]
    public void RecordingPseudonymizer_AssignedToError_ResolvesTheOwner(string message, string expected)
    {
        PseudonymizedRecording result = Pseudonymize([], [Line("", "Error", "UAL238", message)]);

        Assert.Equal(expected, Assert.Single(result.TerminalLog).Message);
    }

    [Fact]
    public void RecordingPseudonymizer_BookmarkCreator_IsPseudonymised_NameKept()
    {
        List<TimelineBookmark> bookmarks =
        [
            new("bm-1", 10, "JD and Pat Smith conflict", "JD"),
            new("bm-2", 20, null, "XY"),
            new("bm-3", 30, "unattributed", null),
            new("bm-4", 40, "go around", "CM"),
            new("bm-5", 50, "empty creator", ""),
        ];

        IReadOnlyList<TimelineBookmark> result = RecordingPseudonymizer.PseudonymizeBookmarks(bookmarks, Participants);

        Assert.Equal(
            [
                new TimelineBookmark("bm-1", 10, "JD and Pat Smith conflict", "A0"),
                new TimelineBookmark("bm-2", 20, null, "??"),
                new TimelineBookmark("bm-3", 30, "unattributed", null),
                new TimelineBookmark("bm-4", 40, "go around", "A1"),
                new TimelineBookmark("bm-5", 50, "empty creator", ""),
            ],
            result
        );
    }

    [Fact]
    public void RecordingPseudonymizer_SamePeople_GetTheSamePseudonymsAsTheServerLog()
    {
        // One participant list, in first-seen order, drives both the server log and the recording.
        IReadOnlyList<LogParticipant> participants = SessionLogAnonymizer.BuildParticipants(
            [("7001234", "PS"), ("1826127", "JD")],
            [("1826127", "Jane Doe")]
        );
        string serverLog = SessionLogAnonymizer.Anonymize(
            "[SendCommand] UAL238: 'FH 270' (from JD)\n[SendCommand] SKW12: 'FH 090' (from PS)\nrealName=Jane Doe",
            participants,
            SessionLogAnonymizer.CommandVerbs
        );

        PseudonymizedRecording result = RecordingPseudonymizer.Pseudonymize(
            [new RecordedCommand(1, "UAL238", "FH 270", "JD", "conn-1"), new RecordedCommand(2, "SKW12", "FH 090", "PS", "conn-2")],
            [Line("OAK_TWR (Jane Doe)", "Chat", "UAL238", "say altitude")],
            participants
        );

        Assert.Equal("[SendCommand] UAL238: 'FH 270' (from A1)\n[SendCommand] SKW12: 'FH 090' (from A0)\nrealName=A1", serverLog);
        Assert.Equal(["A1", "A0"], result.Actions.OfType<RecordedCommand>().Select(c => c.Initials));
        Assert.Equal("OAK_TWR (A1)", Assert.Single(result.TerminalLog).Initials);
    }

    // S3-NCTB-6 (B) | SFO10: eight commands typed by one controller, RY, the last at t=935.
    private const string FixturePath = "TestData/issue-sfo-r10l-offset-recording.yaat-bug-report-bundle.zip";
    private const int ReplayEndSeconds = 940;
    private const int CompareEverySeconds = 60;

    [Fact]
    public void ScrubbedRecording_ReplaysToTheSameSnapshots()
    {
        SessionRecording? recording = RecordingLoader.Load(FixturePath);
        Assert.NotNull(recording);
        IReadOnlyList<LogParticipant> participants = [new LogParticipant("1999999", "Ray Young", "RY")];

        List<RecordedAction> scrubbed = RecordingPseudonymizer.Pseudonymize(recording.Actions, [], participants).Actions;

        Assert.Contains(recording.Actions.OfType<RecordedCommand>(), c => c.Initials == "RY");
        Assert.All(scrubbed.OfType<RecordedCommand>(), c => Assert.Equal("A0", c.Initials));

        SimulationEngine raw = BuildReplay(recording, recording.Actions);
        SimulationEngine pseudonymised = BuildReplay(recording, scrubbed);
        for (int t = 1; t <= ReplayEndSeconds; t++)
        {
            raw.ReplayOneSecond();
            pseudonymised.ReplayOneSecond();
            if ((t % CompareEverySeconds == 0) || (t == ReplayEndSeconds))
            {
                IReadOnlyList<SnapshotDivergence> differences = SnapshotTreeDiff.Compare(raw.CaptureSnapshot(), pseudonymised.CaptureSnapshot());
                Assert.True(differences.Count == 0, $"t={t}: {string.Join("; ", differences.Take(5))}");
            }
        }
    }

    private static SimulationEngine BuildReplay(SessionRecording recording, List<RecordedAction> actions)
    {
        TestVnasData.EnsureInitialized();
        var engine = new SimulationEngine(
            RecordingGroundData.Evergreen("replay determinism of a pseudonymised recording on today's SFO"),
            PushPlanStore.CreateShipped()
        );
        engine.Replay(recording, 0);
        engine.ArmReplay(actions);
        return engine;
    }
}

using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="CommandScopes"/>: the verbs sent without a selected aircraft are exactly the room- and position-addressed
/// kinds' verbs plus GHOST and TIMER, and the verb-to-kind table agrees with what the parser and the classifier make of
/// each verb.
/// </summary>
public class CommandScopesTests
{
    /// <summary>One text per table entry that parses to that verb, for the agreement check.</summary>
    private static readonly Dictionary<CanonicalCommandType, string> Samples = new()
    {
        [CanonicalCommandType.Pause] = "PAUSE",
        [CanonicalCommandType.Unpause] = "UNPAUSE",
        [CanonicalCommandType.SimRate] = "SIMRATE 4",
        [CanonicalCommandType.Add] = "ADD V S P OAK",
        [CanonicalCommandType.SquawkAll] = "SQALL",
        [CanonicalCommandType.SquawkNormalAll] = "SNALL",
        [CanonicalCommandType.SquawkStandbyAll] = "SSALL",
        [CanonicalCommandType.Consolidate] = "CON 2B 4U",
        [CanonicalCommandType.ConsolidateFull] = "CON+ 2B 4U",
        [CanonicalCommandType.Deconsolidate] = "DECON 4U",
        [CanonicalCommandType.SetActivePosition] = "AS 4U",
        [CanonicalCommandType.AcceptAllHandoffs] = "ACCEPTALL",
        [CanonicalCommandType.InitiateHandoffAll] = "HOALL 4U",
        [CanonicalCommandType.CoordinationAutoAck] = "RDAUTO POAK",
        [CanonicalCommandType.TaxiAll] = "TAXIALL 28R",
        [CanonicalCommandType.TdlsOpsConfig] = "TDLSOPS OAK OAKE",
        [CanonicalCommandType.Bookmark] = "BM ADD test",
        [CanonicalCommandType.HoldForRelease] = "HFR OAK",
        [CanonicalCommandType.DisarmHoldForRelease] = "HFROFF OAK",
        [CanonicalCommandType.ReleaseDeparture] = "REL OAK",
        [CanonicalCommandType.ActiveRunways] = "ARWY OAK 28R",
        [CanonicalCommandType.AsdexEnableAllAlerts] = "ASDXALERTS",
        [CanonicalCommandType.GhostTrack] = "GHOST N77GH 37.72 -122.22",
        [CanonicalCommandType.Timer] = "TIMER 90",
    };

    public CommandScopesTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Every table entry is sent without a selection: an entry pointing at an aircraft-scoped kind (other than the named
    /// GHOST / TIMER pair) answers false and fails here.
    /// </summary>
    [Fact]
    public void SendsWithoutSelection_EveryTableVerb() =>
        Assert.All(CommandScopes.VerbKinds.Keys, type => Assert.True(CommandScopes.SendsWithoutSelection(type), $"{type}"));

    /// <summary>The verbs this table makes global on the client that its hand list did not.</summary>
    [Theory]
    [InlineData(CanonicalCommandType.HoldForRelease)]
    [InlineData(CanonicalCommandType.DisarmHoldForRelease)]
    [InlineData(CanonicalCommandType.ReleaseDeparture)]
    [InlineData(CanonicalCommandType.ActiveRunways)]
    [InlineData(CanonicalCommandType.AsdexEnableAllAlerts)]
    public void SendsWithoutSelection_RoomVerbsAddedByTheTable(CanonicalCommandType type) => Assert.True(CommandScopes.SendsWithoutSelection(type));

    [Theory]
    [InlineData(CanonicalCommandType.Cfr)]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.Delete)]
    public void SendsWithoutSelection_NotForAircraftOrCallsignVerbs(CanonicalCommandType type) =>
        Assert.False(CommandScopes.SendsWithoutSelection(type));

    /// <summary>
    /// Every table entry is the kind the classifier gives a parsed sample of that verb, and each sample's verb is the
    /// entry's own (so a sample cannot pass by parsing as a different verb of the same kind).
    /// </summary>
    [Fact]
    public void VerbKinds_AgreeWithTheClassifier()
    {
        Assert.Equal(CommandScopes.VerbKinds.Keys.Order(), Samples.Keys.Order());
        foreach ((CanonicalCommandType type, string sample) in Samples)
        {
            Assert.Equal(type, CommandRegistry.AliasToCanonicType[sample.Split(' ')[0]]);
            ParseResult<ParsedCommand> parsed = CommandParser.Parse(sample);
            Assert.True(parsed.IsSuccess, $"{type}: '{sample}' did not parse: {parsed.Reason}");
            Assert.Equal(CommandScopes.VerbKinds[type], RecordedCommandClassifier.Classify(sample).Kind);
        }
    }

    /// <summary>
    /// A room- or position-addressed kind added without a verb in the table would be sent only with a selection. The check
    /// is per kind, not per verb: it cannot see a second verb added to a kind that already has one, because there is no
    /// per-verb sample text to parse outside the table itself. <see cref="RecordedCommandKind.MalformedGlobal"/> is the
    /// refusal of such a verb, not a verb of its own.
    /// </summary>
    [Fact]
    public void VerbKinds_CoverEveryRoomAddressedKind()
    {
        RecordedCommandKind[] roomKinds =
        [
            .. Enum.GetValues<RecordedCommandKind>()
                .Where(kind => kind != RecordedCommandKind.MalformedGlobal)
                .Where(kind => RecordedCommandClassifier.ScopeOf(kind) is ActionScope.Global or ActionScope.Position),
        ];

        Assert.All(roomKinds, kind => Assert.Contains(kind, CommandScopes.VerbKinds.Values));
    }
}

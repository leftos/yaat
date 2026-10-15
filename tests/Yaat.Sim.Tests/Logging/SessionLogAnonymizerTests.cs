using Xunit;
using Yaat.Sim.Logging;

namespace Yaat.Sim.Tests.Logging;

/// <summary>
/// <see cref="SessionLogAnonymizer"/> must scrub every participant identifier (CID, real name,
/// initials) from an exported server-log slice while leaving non-identifying tokens (callsigns, TCPs)
/// readable and not corrupting identifiers embedded inside larger tokens.
/// </summary>
public class SessionLogAnonymizerTests
{
    private static readonly IReadOnlySet<string> NoKeep = new HashSet<string>();

    [Fact]
    public void Anonymize_ReplacesCidNameAndInitials_WithSamePseudonym()
    {
        var participants = new List<LogParticipant> { new("1826127", "Jane Doe", "JD") };

        string log = string.Join('\n', "[abc123] connected cid=1826127 realName=Jane Doe", "[SendCommand] UAL238: 'FH 270' (from JD)");

        string result = SessionLogAnonymizer.Anonymize(log, participants, NoKeep);

        Assert.DoesNotContain("1826127", result);
        Assert.DoesNotContain("Jane Doe", result);
        Assert.DoesNotContain("(from JD)", result);
        Assert.Contains("cid=A0", result);
        Assert.Contains("realName=A0", result);
        Assert.Contains("(from A0)", result);
        // Aircraft callsign is not an identifier and survives.
        Assert.Contains("UAL238", result);
    }

    [Fact]
    public void Anonymize_AssignsStableSequentialPseudonyms()
    {
        var participants = new List<LogParticipant>();
        for (int i = 0; i < 11; i++)
        {
            participants.Add(new LogParticipant($"cid{i:D2}", RealName: null, Initials: $"I{i:D2}"));
        }

        string log = string.Join('\n', participants.Select(p => $"{p.Cid} acted as {p.Initials}"));

        string result = SessionLogAnonymizer.Anonymize(log, participants, NoKeep);

        // 10 per letter: index 0->A0 .. 9->A9, 10->B0.
        Assert.Contains("A0 acted as A0", result);
        Assert.Contains("A9 acted as A9", result);
        Assert.Contains("B0 acted as B0", result);
        Assert.DoesNotContain("cid00", result);
        Assert.DoesNotContain("cid10", result);
    }

    [Fact]
    public void Anonymize_IsBoundaryAware_DoesNotMangleEmbeddedCid()
    {
        var participants = new List<LogParticipant> { new("1826127", RealName: null, Initials: null) };

        string log = "beacon=18261270 tcp=C1826127 owner-cid=1826127";

        string result = SessionLogAnonymizer.Anonymize(log, participants, NoKeep);

        // Standalone CID redacted; longer digit run and letter-prefixed token left intact.
        Assert.Contains("owner-cid=A0", result);
        Assert.Contains("beacon=18261270", result);
        Assert.Contains("tcp=C1826127", result);
    }

    [Fact]
    public void Anonymize_NeverRedactsSystemMarkers_EvenIfCollectedAsTokens()
    {
        // Scenario auto-positions surface RealName "YAAT" and the RPO/CRC author marker is "CRC" —
        // both are system tokens, never people, so they must survive even when collected.
        var participants = new List<LogParticipant> { new("700123", "YAAT", "CRC") };

        string log = "[SendCommand] N655EX: 'AS 3O DEL' (from CRC) handing 4R to YAAT cid=700123";

        string result = SessionLogAnonymizer.Anonymize(log, participants, NoKeep);

        Assert.Contains("3O", result);
        Assert.Contains("4R", result);
        Assert.Contains("(from CRC)", result);
        Assert.Contains("to YAAT", result);
        // The actual CID is still redacted.
        Assert.Contains("cid=A0", result);
    }

    [Fact]
    public void Anonymize_EmptyParticipants_ReturnsInputUnchanged()
    {
        const string log = "[room] nothing to redact here";

        Assert.Equal(log, SessionLogAnonymizer.Anonymize(log, [], NoKeep));
    }

    [Fact]
    public void Anonymize_LeavesKeepTokens_EvenWhenTheyAreAParticipantsInitials()
    {
        var participants = new List<LogParticipant> { new("1826127", RealName: null, Initials: "CM") };

        string result = SessionLogAnonymizer.Anonymize("UAL238 CM 050 cid=1826127", participants, SessionLogAnonymizer.CommandVerbs);

        Assert.Equal("UAL238 CM 050 cid=A0", result);
    }

    [Fact]
    public void Anonymize_MapsAListedSendersInitialsInTheFromSlot_EvenWhenTheyAreACommandVerb()
    {
        var participants = new List<LogParticipant> { new("1826127", RealName: null, Initials: "CM") };
        string log = string.Join('\n', "[SendCommand] UAL123: 'CM 050' (from CM)", "[SendCommand] UAL456: 'FH 270' (from XY)");

        string result = SessionLogAnonymizer.Anonymize(log, participants, SessionLogAnonymizer.CommandVerbs);

        // A sender the list does not hold is hidden.
        Assert.Equal(string.Join('\n', "[SendCommand] UAL123: 'CM 050' (from A0)", "[SendCommand] UAL456: 'FH 270' (from ??)"), result);
    }

    [Fact]
    public void CommandVerbs_HoldsAliasesAndCanonicalNames_IgnoringCase()
    {
        Assert.Contains("CM", SessionLogAnonymizer.CommandVerbs);
        Assert.Contains("cm", SessionLogAnonymizer.CommandVerbs);
        Assert.Contains("FH", SessionLogAnonymizer.CommandVerbs);
        Assert.Contains("ClimbMaintain", SessionLogAnonymizer.CommandVerbs);
    }

    [Fact]
    public void Anonymize_ReplacesATokenThatEndsInPunctuation()
    {
        var participants = new List<LogParticipant> { new("1826127", "John Q.", "JQ") };

        string result = SessionLogAnonymizer.Anonymize("realName=John Q. joined", participants, NoKeep);

        Assert.Equal("realName=A0 joined", result);
    }

    [Fact]
    public void BuildParticipants_MergesMembersAndCrcIdentitiesByCid()
    {
        (string, string)[] members = [("1826127", "JD"), ("700123", "PS")];
        (string, string)[] crc = [("1826127", "Jane Doe"), ("700123", "Pat Smith")];

        IReadOnlyList<LogParticipant> result = SessionLogAnonymizer.BuildParticipants(members, crc);

        Assert.Equal(2, result.Count);
        LogParticipant jane = result.Single(p => p.Cid == "1826127");
        Assert.Equal("Jane Doe", jane.RealName);
        Assert.Equal("JD", jane.Initials);
        LogParticipant pat = result.Single(p => p.Cid == "700123");
        Assert.Equal("Pat Smith", pat.RealName);
        Assert.Equal("PS", pat.Initials);
    }

    [Fact]
    public void BuildParticipants_SkipsBlankCidsAndOrdersDeterministically()
    {
        (string, string)[] members = [("", "ZZ"), ("700123", "PS"), ("1826127", "JD")];
        (string, string)[] crc = [];

        IReadOnlyList<LogParticipant> result = SessionLogAnonymizer.BuildParticipants(members, crc);

        Assert.Equal(2, result.Count);
        // In first-seen order, blank CID dropped.
        Assert.Equal("700123", result[0].Cid);
        Assert.Equal("1826127", result[1].Cid);
    }

    [Fact]
    public void BuildParticipants_KeepsFirstSeenOrder()
    {
        // A later arrival never renumbers the people already seen: pseudonym index = first-seen position.
        (string, string)[] members = [("700123", "PS"), ("1826127", "JD"), ("700123", "XX")];
        (string, string)[] crc = [("555", "Zed Outsider"), ("1826127", "Jane Doe")];

        IReadOnlyList<LogParticipant> result = SessionLogAnonymizer.BuildParticipants(members, crc);

        Assert.Equal(
            [
                new LogParticipant("700123", null, "PS") { OtherInitials = ["XX"] },
                new LogParticipant("1826127", "Jane Doe", "JD"),
                new LogParticipant("555", "Zed Outsider", null),
            ],
            result
        );
    }
}

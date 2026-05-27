using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class SidStarNameNormalizerTests
{
    private static readonly ProcedurePattern Eagul5Star = new("EAGUL5", ProcedureKind.Star, "EAGUL", "5");
    private static readonly ProcedurePattern Suzan2Sid = new("SUZAN2", ProcedureKind.Sid, "SUZAN", "2");
    private static readonly ProcedurePattern StradoStar = new("STRADO", ProcedureKind.Star, "STRADO", "");
    private static readonly ProcedurePattern Hhood5Star = new("HHOOD5", ProcedureKind.Star, "HHOOD", "5");

    [Fact]
    public void Collapse_StarPhraseWithDigit_EmitsCanonical()
    {
        var tokens = new List<string> { "eagul", "5", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Eagul5Star]);
        Assert.Equal(["EAGUL5", "arrival"], output);
    }

    [Fact]
    public void Collapse_SidPhraseWithDigit_EmitsCanonical()
    {
        var tokens = new List<string> { "suzan", "2", "departure" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Suzan2Sid]);
        Assert.Equal(["SUZAN2", "departure"], output);
    }

    [Fact]
    public void Collapse_StarWithLeadingContext_StillCollapses()
    {
        var tokens = new List<string> { "descend", "via", "the", "eagul", "5", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Eagul5Star]);
        Assert.Equal(["descend", "via", "the", "EAGUL5", "arrival"], output);
    }

    [Fact]
    public void Collapse_KindMismatch_DoesNotCollapse()
    {
        // STAR keyword "arrival" with SID-only procedure list — must not collapse.
        var tokens = new List<string> { "suzan", "2", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Suzan2Sid]);
        Assert.Equal(["suzan", "2", "arrival"], output);
    }

    [Fact]
    public void Collapse_NoTrailingKeyword_DoesNotCollapse()
    {
        // Without "arrival"/"departure" the normalizer can't be confident about the slot.
        var tokens = new List<string> { "descend", "via", "the", "eagul", "5" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Eagul5Star]);
        Assert.Equal(["descend", "via", "the", "eagul", "5"], output);
    }

    [Fact]
    public void Collapse_WhisperMishear_FuzzyMatchesViaPhonetic()
    {
        // "eagle" is a common Whisper transcription for EAGUL — PhoneticFixMatcher should resolve.
        var tokens = new List<string> { "eagle", "5", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Eagul5Star]);
        Assert.Equal(["EAGUL5", "arrival"], output);
    }

    [Fact]
    public void Collapse_DigitMismatch_DoesNotCollapse()
    {
        // Base matches but the digit suffix doesn't — the procedure name isn't a match.
        var tokens = new List<string> { "eagul", "3", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Eagul5Star]);
        Assert.Equal(["eagul", "3", "arrival"], output);
    }

    [Fact]
    public void Collapse_NoDigitSuffix_CollapsesBareBase()
    {
        // Procedure with no digit suffix (e.g. STRADO).
        var tokens = new List<string> { "strado", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [StradoStar]);
        Assert.Equal(["STRADO", "arrival"], output);
    }

    [Fact]
    public void Collapse_EmptyProcedures_NoOps()
    {
        var tokens = new List<string> { "eagul", "5", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, []);
        Assert.Equal(["eagul", "5", "arrival"], output);
    }

    [Fact]
    public void Collapse_MultipleProcedures_LongestKindMatchWins()
    {
        var tokens = new List<string> { "descend", "via", "the", "hhood", "5", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Eagul5Star, Hhood5Star, Suzan2Sid]);
        Assert.Equal(["descend", "via", "the", "HHOOD5", "arrival"], output);
    }

    [Fact]
    public void Collapse_TwoProceduresInOneTranscript_BothCollapse()
    {
        var tokens = new List<string> { "suzan", "2", "departure", "then", "eagul", "5", "arrival" };
        var output = SidStarNameNormalizer.Collapse(tokens, [Eagul5Star, Suzan2Sid]);
        Assert.Equal(["SUZAN2", "departure", "then", "EAGUL5", "arrival"], output);
    }
}

using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class NatoLetterNormalizerTests
{
    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> TaxiwaySet(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Empty_Input_Returns_Empty()
    {
        var result = NatoLetterNormalizer.Collapse([], EmptySet);
        Assert.Empty(result);
    }

    [Fact]
    public void Non_Nato_Passes_Through_Unchanged()
    {
        var input = new[] { "taxi", "via", "hold", "short" };
        var result = NatoLetterNormalizer.Collapse(input, EmptySet);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Single_Nato_Word_Collapses_To_Single_Letter()
    {
        var result = NatoLetterNormalizer.Collapse(["tango"], EmptySet);
        Assert.Equal(["T"], result);
    }

    [Fact]
    public void Run_Of_Nato_Words_Collapses_Each_To_Single_Letter_When_Set_Empty()
    {
        var result = NatoLetterNormalizer.Collapse(["tango", "uniform", "whiskey"], EmptySet);
        Assert.Equal(["T", "U", "W"], result);
    }

    [Fact]
    public void Mixed_Nato_And_Non_Nato_Only_Collapses_Nato_Runs()
    {
        var input = new[] { "taxi", "via", "tango", "uniform", "whiskey" };
        var result = NatoLetterNormalizer.Collapse(input, EmptySet);
        Assert.Equal(["taxi", "via", "T", "U", "W"], result);
    }

    [Fact]
    public void Multi_Letter_Name_In_Set_Is_Used()
    {
        // Airport has taxiway "TE" — "tango echo" should collapse to single "TE" token.
        var result = NatoLetterNormalizer.Collapse(["tango", "echo"], TaxiwaySet("TE"));
        Assert.Equal(["TE"], result);
    }

    [Fact]
    public void Multi_Letter_Name_Absent_From_Set_Splits_To_Single_Letters()
    {
        // Airport has separate T and E taxiways, no TE — "tango echo" must split.
        var result = NatoLetterNormalizer.Collapse(["tango", "echo"], TaxiwaySet("T", "E"));
        Assert.Equal(["T", "E"], result);
    }

    [Fact]
    public void Longest_Match_Wins_When_Multiple_Prefixes_Exist()
    {
        // Both "TE" and "TEL" exist; "tango echo lima" should collapse to "TEL" (longest).
        var result = NatoLetterNormalizer.Collapse(["tango", "echo", "lima"], TaxiwaySet("TE", "TEL"));
        Assert.Equal(["TEL"], result);
    }

    [Fact]
    public void Greedy_Split_Falls_Back_After_Longest_Match_Consumed()
    {
        // "tango echo mike" with set {TE}: greedy takes TE then M on its own.
        var result = NatoLetterNormalizer.Collapse(["tango", "echo", "mike"], TaxiwaySet("TE"));
        Assert.Equal(["TE", "M"], result);
    }

    [Fact]
    public void Run_Longer_Than_Max_Still_Splits_Cleanly()
    {
        // 5-letter run with no multi-letter taxiways in the set: every letter splits individually.
        var result = NatoLetterNormalizer.Collapse(["alpha", "bravo", "charlie", "delta", "echo"], EmptySet);
        Assert.Equal(["A", "B", "C", "D", "E"], result);
    }

    [Fact]
    public void Case_Insensitive_Input_Tokens()
    {
        // Upstream usually lowercases, but the normalizer should handle mixed case defensively.
        var result = NatoLetterNormalizer.Collapse(["Tango", "ECHO"], TaxiwaySet("TE"));
        Assert.Equal(["TE"], result);
    }

    [Fact]
    public void November_In_Isolation_Collapses_To_N()
    {
        // Callsign extraction would normally remove "november <digits>"; a surviving isolated
        // "november" in a taxi context is treated as taxiway N. This is the expected behavior
        // documented in NatoLetterNormalizer's remarks.
        var result = NatoLetterNormalizer.Collapse(["taxi", "via", "november"], EmptySet);
        Assert.Equal(["taxi", "via", "N"], result);
    }
}

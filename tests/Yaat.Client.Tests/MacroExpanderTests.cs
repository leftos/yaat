using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class MacroExpanderTests
{
    private static readonly List<MacroDefinition> Macros =
    [
        new() { Name = "BAYTOUR", Expansion = "DCT VPCOL VPCHA VPMID" },
        new() { Name = "HC", Expansion = "FH $1, CM $2" },
        new() { Name = "FC", Expansion = "FH $hdg, CM $alt" },
        new() { Name = "BIG", Expansion = "FH $1, CM $2, SPD $3, SQ $4, DCT $5, TL $6, TR $7, DM $8, RL $9, RR $10" },
    ];

    [Fact]
    public void NoHash_ReturnsNull()
    {
        var result = MacroExpander.TryExpand("FH 270", Macros, out var error);
        Assert.Null(result);
        Assert.Null(error);
    }

    [Fact]
    public void SimpleNoParamMacro_Expands()
    {
        var result = MacroExpander.TryExpand("#BAYTOUR", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void PositionalParams_Substituted()
    {
        var result = MacroExpander.TryExpand("#HC 270 5000", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }

    [Fact]
    public void NamedParams_Substituted()
    {
        var result = MacroExpander.TryExpand("#FC 270 5000", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }

    [Fact]
    public void MissingParams_ReturnsError()
    {
        var result = MacroExpander.TryExpand("#HC 270", Macros, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("expects 2", error);
    }

    [Fact]
    public void MissingNamedParams_ErrorShowsNames()
    {
        var result = MacroExpander.TryExpand("#FC 270", Macros, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("$hdg", error);
        Assert.Contains("$alt", error);
    }

    [Fact]
    public void UnknownMacro_ReturnsError()
    {
        var result = MacroExpander.TryExpand("#UNKNOWN", Macros, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("Unknown macro", error);
    }

    [Fact]
    public void CaseInsensitiveLookup()
    {
        var result = MacroExpander.TryExpand("#baytour", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void MacroWithinCompound_ExpandsOnlyMacro()
    {
        var result = MacroExpander.TryExpand("FH 270; #BAYTOUR", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270; DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void MacroBeforeCompound_ExpandsCorrectly()
    {
        var result = MacroExpander.TryExpand("#HC 270 5000; DCT SUNOL", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000; DCT SUNOL", result);
    }

    [Fact]
    public void HighParamNumbers_NoClash()
    {
        var result = MacroExpander.TryExpand("#BIG A B C D E F G H I J", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH A, CM B, SPD C, SQ D, DCT E, TL F, TR G, DM H, RL I, RR J", result);
    }

    [Fact]
    public void MacroAfterComma_Expands()
    {
        var result = MacroExpander.TryExpand("FH 270,#BAYTOUR", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270,DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void HashInMiddleOfWord_NotTreatedAsMacro()
    {
        // # not preceded by boundary — should not expand
        var result = MacroExpander.TryExpand("ABC#BAYTOUR", Macros, out var error);
        Assert.Null(result);
        Assert.Null(error);
    }

    [Fact]
    public void NestedMacro_ExpandsRecursively()
    {
        var macros = new List<MacroDefinition>
        {
            new() { Name = "INNER", Expansion = "FH 270, CM 5000" },
            new() { Name = "OUTER", Expansion = "#INNER; DCT SUNOL" },
        };
        var result = MacroExpander.TryExpand("#OUTER", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000; DCT SUNOL", result);
    }

    [Fact]
    public void NestedMacro_ThreeDeep()
    {
        var macros = new List<MacroDefinition>
        {
            new() { Name = "A", Expansion = "FH 270" },
            new() { Name = "B", Expansion = "#A, CM 5000" },
            new() { Name = "C", Expansion = "#B; DCT SUNOL" },
        };
        var result = MacroExpander.TryExpand("#C", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000; DCT SUNOL", result);
    }

    [Fact]
    public void SelfReferencingMacro_StabilizesWithoutInfiniteLoop()
    {
        // #SELF expands to "#SELF" — the result equals the input, so expansion stops
        var macros = new List<MacroDefinition>
        {
            new() { Name = "SELF", Expansion = "#SELF" },
        };
        var result = MacroExpander.TryExpand("#SELF", macros, out var error);
        // First pass: "#SELF" → "#SELF" (same string) → no effective change → returns null
        Assert.Null(error);
        Assert.Null(result);
    }

    [Fact]
    public void NestedMacroWithParams_ExpandsRecursively()
    {
        var macros = new List<MacroDefinition>
        {
            new() { Name = "HDG", Expansion = "FH $1" },
            new() { Name = "HCLI", Expansion = "#HDG $1, CM $2" },
        };
        var result = MacroExpander.TryExpand("#HCLI 270 5000", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }
}

public class MacroDefinitionTests
{
    [Fact]
    public void ParameterCount_NoParams()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "DCT FIX1 FIX2" };
        Assert.Equal(0, def.ParameterCount);
    }

    [Fact]
    public void ParameterCount_PositionalParams()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH $1, CM $2" };
        Assert.Equal(2, def.ParameterCount);
    }

    [Fact]
    public void ParameterCount_NamedParams()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH $hdg, CM $alt" };
        Assert.Equal(2, def.ParameterCount);
    }

    [Fact]
    public void ParameterNames_PositionalOrder()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH $1, CM $2" };
        Assert.Equal(new[] { "1", "2" }, def.ParameterNames);
    }

    [Fact]
    public void ParameterNames_NamedOrder()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH $hdg, CM $alt" };
        Assert.Equal(new[] { "hdg", "alt" }, def.ParameterNames);
    }

    [Fact]
    public void ParameterNames_DuplicatesCollapsed()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH $hdg, TL $hdg" };
        Assert.Single(def.ParameterNames);
        Assert.Equal("hdg", def.ParameterNames[0]);
    }

    [Theory]
    [InlineData("BAYTOUR", true)]
    [InlineData("HC", true)]
    [InlineData("my_macro", true)]
    [InlineData("_private", true)]
    [InlineData("1BAD", false)]
    [InlineData("", false)]
    [InlineData("HAS SPACE", false)]
    public void IsValidName(string name, bool expected)
    {
        Assert.Equal(expected, MacroDefinition.IsValidName(name));
    }
}

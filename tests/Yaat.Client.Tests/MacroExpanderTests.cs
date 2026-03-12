using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class MacroExpanderTests
{
    private static readonly List<MacroDefinition> Macros =
    [
        new() { Name = "BAYTOUR", Expansion = "DCT VPCOL VPCHA VPMID" },
        new() { Name = "HC", Expansion = "FH &1, CM &2" },
        new() { Name = "FC", Expansion = "FH &hdg, CM &alt" },
        new() { Name = "BIG", Expansion = "FH &1, CM &2, SPD &3, SQ &4, DCT &5, TL &6, TR &7, DM &8, RL &9, RR &10" },
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
        var result = MacroExpander.TryExpand("!BAYTOUR", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void PositionalParams_Substituted()
    {
        var result = MacroExpander.TryExpand("!HC 270 5000", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }

    [Fact]
    public void NamedParams_Substituted()
    {
        var result = MacroExpander.TryExpand("!FC 270 5000", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }

    [Fact]
    public void MissingParams_ReturnsError()
    {
        var result = MacroExpander.TryExpand("!HC 270", Macros, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("expects 2", error);
    }

    [Fact]
    public void MissingNamedParams_ErrorShowsNames()
    {
        var result = MacroExpander.TryExpand("!FC 270", Macros, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("&hdg", error);
        Assert.Contains("&alt", error);
    }

    [Fact]
    public void UnknownMacro_ReturnsError()
    {
        var result = MacroExpander.TryExpand("!UNKNOWN", Macros, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("Unknown macro", error);
    }

    [Fact]
    public void CaseInsensitiveLookup()
    {
        var result = MacroExpander.TryExpand("!baytour", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void MacroWithinCompound_ExpandsOnlyMacro()
    {
        var result = MacroExpander.TryExpand("FH 270; !BAYTOUR", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270; DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void MacroBeforeCompound_ExpandsCorrectly()
    {
        var result = MacroExpander.TryExpand("!HC 270 5000; DCT SUNOL", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000; DCT SUNOL", result);
    }

    [Fact]
    public void HighParamNumbers_NoClash()
    {
        var result = MacroExpander.TryExpand("!BIG A B C D E F G H I J", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH A, CM B, SPD C, SQ D, DCT E, TL F, TR G, DM H, RL I, RR J", result);
    }

    [Fact]
    public void MacroAfterComma_Expands()
    {
        var result = MacroExpander.TryExpand("FH 270,!BAYTOUR", Macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270,DCT VPCOL VPCHA VPMID", result);
    }

    [Fact]
    public void BangInMiddleOfWord_NotTreatedAsMacro()
    {
        // ! not preceded by boundary — should not expand
        var result = MacroExpander.TryExpand("ABC!BAYTOUR", Macros, out var error);
        Assert.Null(result);
        Assert.Null(error);
    }

    [Fact]
    public void NestedMacro_ExpandsRecursively()
    {
        var macros = new List<MacroDefinition>
        {
            new() { Name = "INNER", Expansion = "FH 270, CM 5000" },
            new() { Name = "OUTER", Expansion = "!INNER; DCT SUNOL" },
        };
        var result = MacroExpander.TryExpand("!OUTER", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000; DCT SUNOL", result);
    }

    [Fact]
    public void NestedMacro_ThreeDeep()
    {
        var macros = new List<MacroDefinition>
        {
            new() { Name = "A", Expansion = "FH 270" },
            new() { Name = "B", Expansion = "!A, CM 5000" },
            new() { Name = "C", Expansion = "!B; DCT SUNOL" },
        };
        var result = MacroExpander.TryExpand("!C", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000; DCT SUNOL", result);
    }

    [Fact]
    public void SelfReferencingMacro_StabilizesWithoutInfiniteLoop()
    {
        // !SELF expands to "!SELF" — the result equals the input, so expansion stops
        var macros = new List<MacroDefinition>
        {
            new() { Name = "SELF", Expansion = "!SELF" },
        };
        var result = MacroExpander.TryExpand("!SELF", macros, out var error);
        // First pass: "!SELF" → "!SELF" (same string) → no effective change → returns null
        Assert.Null(error);
        Assert.Null(result);
    }

    [Fact]
    public void NestedMacroWithParams_ExpandsRecursively()
    {
        var macros = new List<MacroDefinition>
        {
            new() { Name = "HDG", Expansion = "FH &1" },
            new() { Name = "HCLI", Expansion = "!HDG &1, CM &2" },
        };
        var result = MacroExpander.TryExpand("!HCLI 270 5000", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }

    [Fact]
    public void ExplicitParams_OrderDiffersFromExpansion()
    {
        // Name declares &alt first, &hdg second — so input arg 1 maps to alt, arg 2 maps to hdg
        var macros = new List<MacroDefinition>
        {
            new() { Name = "HC &alt &hdg", Expansion = "FH &hdg, CM &alt" },
        };
        var result = MacroExpander.TryExpand("!HC 5000 270", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }

    [Fact]
    public void ExplicitParams_FindMacroMatchesBaseName()
    {
        // FindMacro should match "HC" even when macro name is "HC &hdg &alt"
        var macros = new List<MacroDefinition>
        {
            new() { Name = "HC &hdg &alt", Expansion = "FH &hdg, CM &alt" },
        };
        var result = MacroExpander.TryExpand("!HC 270 5000", macros, out var error);
        Assert.Null(error);
        Assert.Equal("FH 270, CM 5000", result);
    }

    [Fact]
    public void ExplicitParams_InvalidExpansion_ReturnsError()
    {
        // &hgd is a typo — declared &hdg but expansion uses &hgd
        var macros = new List<MacroDefinition>
        {
            new() { Name = "HC &hdg &alt", Expansion = "FH &hgd, CM &alt" },
        };
        var result = MacroExpander.TryExpand("!HC 270 5000", macros, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("&hdg", error);
        Assert.Contains("not found in expansion", error);
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
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH &1, CM &2" };
        Assert.Equal(2, def.ParameterCount);
    }

    [Fact]
    public void ParameterCount_NamedParams()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH &hdg, CM &alt" };
        Assert.Equal(2, def.ParameterCount);
    }

    [Fact]
    public void ParameterNames_PositionalOrder()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH &1, CM &2" };
        Assert.Equal(new[] { "1", "2" }, def.ParameterNames);
    }

    [Fact]
    public void ParameterNames_NamedOrder()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH &hdg, CM &alt" };
        Assert.Equal(new[] { "hdg", "alt" }, def.ParameterNames);
    }

    [Fact]
    public void ParameterNames_DuplicatesCollapsed()
    {
        var def = new MacroDefinition { Name = "TEST", Expansion = "FH &hdg, TL &hdg" };
        Assert.Single(def.ParameterNames);
        Assert.Equal("hdg", def.ParameterNames[0]);
    }

    [Fact]
    public void BaseName_PlainName_ReturnsSame()
    {
        var def = new MacroDefinition { Name = "HC", Expansion = "FH &1" };
        Assert.Equal("HC", def.BaseName);
    }

    [Fact]
    public void BaseName_WithExplicitParams_ReturnsFirstToken()
    {
        var def = new MacroDefinition { Name = "HC &hdg &alt", Expansion = "FH &hdg, CM &alt" };
        Assert.Equal("HC", def.BaseName);
    }

    [Fact]
    public void HasExplicitParameters_PlainName_ReturnsFalse()
    {
        var def = new MacroDefinition { Name = "HC", Expansion = "FH &1, CM &2" };
        Assert.False(def.HasExplicitParameters);
    }

    [Fact]
    public void HasExplicitParameters_WithParams_ReturnsTrue()
    {
        var def = new MacroDefinition { Name = "HC &hdg &alt", Expansion = "FH &hdg, CM &alt" };
        Assert.True(def.HasExplicitParameters);
    }

    [Fact]
    public void ParameterNames_ExplicitParams_ReturnsDeclarationOrder()
    {
        // Name declares &alt before &hdg — that order should be used regardless of expansion order
        var def = new MacroDefinition { Name = "HC &alt &hdg", Expansion = "FH &hdg, CM &alt" };
        Assert.Equal(new[] { "alt", "hdg" }, def.ParameterNames);
    }

    [Theory]
    [InlineData("BAYTOUR", true)]
    [InlineData("HC", true)]
    [InlineData("my_macro", true)]
    [InlineData("_private", true)]
    [InlineData("1BAD", false)]
    [InlineData("", false)]
    [InlineData("HAS SPACE", false)] // second token not a &param
    [InlineData("HC &hdg &alt", true)]
    [InlineData("HC &hdg &hdg", false)] // duplicate param
    [InlineData("HC &1bad", false)] // param starts with digit
    public void IsValidName(string name, bool expected)
    {
        Assert.Equal(expected, MacroDefinition.IsValidName(name));
    }

    [Fact]
    public void Validate_ExplicitParamNotInExpansion_ReturnsError()
    {
        var def = new MacroDefinition { Name = "HC &hdg &alt", Expansion = "FH &hgd, CM &alt" };
        var error = def.Validate();
        Assert.NotNull(error);
        Assert.Contains("&hdg", error);
    }

    [Fact]
    public void Validate_AllExplicitParamsUsed_ReturnsNull()
    {
        var def = new MacroDefinition { Name = "HC &hdg &alt", Expansion = "FH &hdg, CM &alt" };
        Assert.Null(def.Validate());
    }

    [Fact]
    public void Validate_InferredParams_AlwaysNull()
    {
        var def = new MacroDefinition { Name = "HC", Expansion = "FH &1, CM &2" };
        Assert.Null(def.Validate());
    }

    [Fact]
    public void ExtractBaseName_PlainName()
    {
        Assert.Equal("HC", MacroDefinition.ExtractBaseName("HC"));
    }

    [Fact]
    public void ExtractBaseName_WithParams()
    {
        Assert.Equal("HC", MacroDefinition.ExtractBaseName("HC &hdg &alt"));
    }
}

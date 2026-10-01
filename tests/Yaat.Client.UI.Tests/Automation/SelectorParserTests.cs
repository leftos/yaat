using Xunit;
using Yaat.Client.Automation.Selectors;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>The selector grammar, one test per form, plus the forms it rejects.</summary>
public sealed class SelectorParserTests
{
    private static CompoundSelector SingleCompound(string selector)
    {
        SelectorPath path = Assert.Single(SelectorParser.Parse(selector).Alternatives);
        return Assert.Single(path.Steps).Compound;
    }

    [Fact]
    public void Type_ParsesTypeName()
    {
        CompoundSelector compound = SingleCompound("Button");

        Assert.Equal("Button", compound.TypeName);
        Assert.Null(compound.NodeId);
        Assert.Empty(compound.Filters);
    }

    [Fact]
    public void Universal_WithFilter_HasNoTypeName()
    {
        CompoundSelector compound = SingleCompound("*[Name=Save]");

        Assert.Null(compound.TypeName);
        Assert.Equal(new AttributeFilter("Name", AttrOp.Equal, "Save", false), Assert.Single(compound.Filters));
    }

    [Fact]
    public void HashName_IsANameFilter()
    {
        CompoundSelector compound = SingleCompound("Button#Save");

        Assert.Equal("Button", compound.TypeName);
        Assert.Equal(new AttributeFilter("Name", AttrOp.Equal, "Save", false), Assert.Single(compound.Filters));
    }

    [Fact]
    public void HashNumber_IsANodeId()
    {
        CompoundSelector compound = SingleCompound("#42");

        Assert.Equal(42, compound.NodeId);
        Assert.Empty(compound.Filters);
    }

    [Theory]
    [InlineData("Button[Text=Save]", AttrOp.Equal)]
    [InlineData("Button[Text*=Save]", AttrOp.Contains)]
    [InlineData("Button[Text^=Save]", AttrOp.StartsWith)]
    [InlineData("Button[Text$=Save]", AttrOp.EndsWith)]
    public void AttributeOperators_Parse(string selector, AttrOp op) =>
        Assert.Equal(new AttributeFilter("Text", op, "Save", false), Assert.Single(SingleCompound(selector).Filters));

    [Fact]
    public void Attribute_QuotedValueAndDottedPath_Parse()
    {
        Assert.Equal(
            new AttributeFilter("Tag.Length", AttrOp.Equal, "Sign in", false),
            Assert.Single(SingleCompound("Button[Tag.Length=\"Sign in\"]").Filters)
        );
    }

    [Fact]
    public void DataContextAttribute_StripsThePrefixAndFlagsIt() =>
        Assert.Equal(
            new AttributeFilter("Callsign", AttrOp.Equal, "AAL123", true),
            Assert.Single(SingleCompound("ListBoxItem[dc.Callsign=AAL123]").Filters)
        );

    [Theory]
    [InlineData("ListBox ListBoxItem", Combinator.Descendant)]
    [InlineData("ListBox >> ListBoxItem", Combinator.Descendant)]
    [InlineData("ListBox > ListBoxItem", Combinator.Child)]
    [InlineData("ListBox>ListBoxItem", Combinator.Child)]
    public void Combinators_Parse(string selector, Combinator combinator)
    {
        SelectorPath path = Assert.Single(SelectorParser.Parse(selector).Alternatives);

        Assert.Equal(2, path.Steps.Count);
        Assert.Equal(Combinator.Self, path.Steps[0].Combinator);
        Assert.Equal("ListBox", path.Steps[0].Compound.TypeName);
        Assert.Equal(combinator, path.Steps[1].Combinator);
        Assert.Equal("ListBoxItem", path.Steps[1].Compound.TypeName);
    }

    [Fact]
    public void Nth_ParsesItsArgument() => Assert.Equal(new PseudoFilter("nth", "2"), Assert.Single(SingleCompound("ListBoxItem:nth(2)").Filters));

    [Fact]
    public void Pseudo_HyphenatedWithQuotedArgument_AndBare_Parse()
    {
        CompoundSelector compound = SingleCompound("Button:has-text(\"Sign in\"):enabled");

        Assert.Equal([new PseudoFilter("has-text", "Sign in"), new PseudoFilter("enabled", null)], compound.Filters);
    }

    [Fact]
    public void Comma_SeparatesAlternatives()
    {
        ParsedSelector parsed = SelectorParser.Parse("Button, MenuItem");

        Assert.Equal(["Button", "MenuItem"], parsed.Alternatives.Select(path => Assert.Single(path.Steps).Compound.TypeName));
    }

    [Fact]
    public void DataContextPredicate_IsRejected() => Assert.Throws<SelectorParseException>(() => SelectorParser.Parse("ListBoxItem[dc:'Id == 42']"));

    [Theory]
    [InlineData("", 0)]
    [InlineData("Button[", 7)]
    [InlineData("Button[Name]", 11)]
    [InlineData("Button[Name='Save]", 12)]
    [InlineData("Button@", 6)]
    public void Malformed_ThrowsWithPosition(string selector, int position)
    {
        SelectorParseException ex = Assert.Throws<SelectorParseException>(() => SelectorParser.Parse(selector));

        Assert.Equal(position, ex.Position);
    }

    [Theory]
    [InlineData("#Form #Save", "Name=Form", "Name=Save")]
    [InlineData("ListBox [dc.X=Y]", "ListBox", "dc.X=Y")]
    [InlineData("Window :enabled", "Window", ":enabled")]
    public void WhitespaceBeforeAFilter_IsADescendantCombinator(string selector, string first, string second)
    {
        SelectorPath path = Assert.Single(SelectorParser.Parse(selector).Alternatives);

        Assert.Equal(2, path.Steps.Count);
        Assert.Equal(Combinator.Descendant, path.Steps[1].Combinator);
        Assert.Equal(first, Describe(path.Steps[0].Compound));
        Assert.Equal(second, Describe(path.Steps[1].Compound));
    }

    // A compound with a type name or one filter, written back in selector-like shorthand.
    private static string Describe(CompoundSelector compound) =>
        compound.TypeName
        ?? Assert.Single(compound.Filters) switch
        {
            AttributeFilter attribute => $"{(attribute.IsDataContext ? "dc." : "")}{attribute.Path}={attribute.Value}",
            PseudoFilter pseudo => $":{pseudo.Name}",
            _ => "?",
        };

    [Fact]
    public void BareUniversal_Parses()
    {
        CompoundSelector compound = SingleCompound("*");

        Assert.Null(compound.TypeName);
        Assert.Null(compound.NodeId);
        Assert.Empty(compound.Filters);
    }

    [Theory]
    [InlineData("* Button", Combinator.Descendant)]
    [InlineData("#Form > *", Combinator.Child)]
    public void Universal_InAPath_Parses(string selector, Combinator combinator)
    {
        SelectorPath path = Assert.Single(SelectorParser.Parse(selector).Alternatives);

        Assert.Equal(2, path.Steps.Count);
        Assert.Equal(combinator, path.Steps[1].Combinator);
    }

    [Theory]
    [InlineData("Button:bogus", 7)]
    [InlineData("Button:nth", 7)]
    [InlineData("Button:nth()", 7)]
    [InlineData("Button:nth(x)", 11)]
    [InlineData("Button:nth(-1)", 11)]
    [InlineData("Button:nth(1.5)", 11)]
    [InlineData("Button:nth(0) > TextBlock", 7)]
    [InlineData("Button:has-text", 7)]
    [InlineData("Button:role()", 7)]
    public void InvalidPseudoClass_ThrowsWithPosition(string selector, int position)
    {
        SelectorParseException ex = Assert.Throws<SelectorParseException>(() => SelectorParser.Parse(selector));

        Assert.Equal(position, ex.Position);
    }

    [Fact]
    public void SecondNodeId_ThrowsWithPosition()
    {
        SelectorParseException ex = Assert.Throws<SelectorParseException>(() => SelectorParser.Parse("#42#43"));

        Assert.Equal(4, ex.Position);
    }

    [Theory]
    [InlineData("Button[Width=1.5]", "Width", "1.5")]
    [InlineData("Button[Name=my-button]", "Name", "my-button")]
    [InlineData("Button[Tag=-3]", "Tag", "-3")]
    [InlineData("Button[Name = Save ]", "Name", "Save")]
    public void UnquotedAttributeValue_RunsToWhitespaceOrBracket(string selector, string path, string value) =>
        Assert.Equal(new AttributeFilter(path, AttrOp.Equal, value, false), Assert.Single(SingleCompound(selector).Filters));

    [Fact]
    public void MissingClosingBracket_NamesTheCharacter()
    {
        SelectorParseException ex = Assert.Throws<SelectorParseException>(() => SelectorParser.Parse("Button[Name=Save"));

        Assert.Contains("expected ']'", ex.Message, StringComparison.Ordinal);
    }
}

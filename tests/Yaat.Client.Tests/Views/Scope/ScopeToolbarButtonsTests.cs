using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The Scope toolbar's button model: one ordered list, each button with an id, a label and a kind.</summary>
public class ScopeToolbarButtonsTests
{
    private static IEnumerable<ScopeButton> Flatten(IEnumerable<ScopeButton> buttons) => buttons.SelectMany(b => Flatten(b.Children).Prepend(b));

    [Fact]
    public void TheBar_IsTemplateThenRange()
    {
        Assert.Equal([ScopeButtonId.Template, ScopeButtonId.Range], ScopeToolbarButtons.All.Select(b => b.Id));
        Assert.Equal(["TEMPLATE", "RANGE"], ScopeToolbarButtons.All.Select(b => b.Label));
    }

    [Fact]
    public void ButtonIds_AreUnique()
    {
        ScopeButtonId[] ids = [.. Flatten(ScopeToolbarButtons.All).Select(b => b.Id)];

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void EveryButton_HasALabelAndAKind()
    {
        foreach (ScopeButton button in Flatten(ScopeToolbarButtons.All))
        {
            Assert.False(string.IsNullOrWhiteSpace(button.Label), $"{button.Id} has no label");
            Assert.True(Enum.IsDefined(button.Kind), $"{button.Id} has no kind");
        }
    }

    [Fact]
    public void Template_IsAMenuOfTheFourTemplates_InOrder()
    {
        ScopeButton template = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Template);

        Assert.Equal(ScopeButtonKind.Menu, template.Kind);
        Assert.Equal(
            [ScopeTemplates.StarsId, ScopeTemplates.EramId, ScopeTemplates.MixedId, ScopeTemplates.EuroScopeId],
            template.Children.Select(c => c.Value)
        );
        Assert.Equal(["STARS", "ERAM", "MIXED", "EUROSCOPE"], template.Children.Select(c => c.Label));
        Assert.All(template.Children, c => Assert.Equal(ScopeButtonKind.Command, c.Kind));
    }

    [Fact]
    public void Range_IsASpinner_WithNoChildren()
    {
        ScopeButton range = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Range);

        Assert.Equal(ScopeButtonKind.Spinner, range.Kind);
        Assert.Empty(range.Children);
    }
}

using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The Scope toolbar's button model: one ordered list, each button with an id, a label and a kind.</summary>
public class ScopeToolbarButtonsTests
{
    private static IEnumerable<ScopeButton> Flatten(IEnumerable<ScopeButton> buttons) => buttons.SelectMany(b => Flatten(b.Children).Prepend(b));

    [Fact]
    public void TheBar_IsTemplateRangeThenViews()
    {
        Assert.Equal([ScopeButtonId.Template, ScopeButtonId.Range, ScopeButtonId.Views], ScopeToolbarButtons.All.Select(b => b.Id));
        Assert.Equal(["TEMPLATE", "RANGE", "VIEWS"], ScopeToolbarButtons.All.Select(b => b.Label));
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
        Assert.Null(range.Command);
    }

    [Fact]
    public void Views_IsAMenuOfTheSsaAndConflictToggles()
    {
        ScopeButton views = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Views);

        Assert.Equal(ScopeButtonKind.Menu, views.Kind);
        Assert.Null(views.Command);
        Assert.Equal([ScopeButtonId.ViewSsa, ScopeButtonId.ViewCa], views.Children.Select(c => c.Id));
        Assert.Equal(["SSA", "LA/CA/MCI"], views.Children.Select(c => c.Label));
        Assert.Equal(["ssa", "ca"], views.Children.Select(c => c.Value));
        Assert.Equal([".view ssa", ".view ca"], views.Children.Select(c => c.Command));
        Assert.All(views.Children, c => Assert.Equal(ScopeButtonKind.Toggle, c.Kind));
    }

    [Fact]
    public void EveryTemplatePick_CarriesItsScopeTemplateCommand()
    {
        ScopeButton template = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Template);

        Assert.Null(template.Command);
        Assert.All(template.Children, c => Assert.Equal($".scope template {c.Value}", c.Command));
    }
}

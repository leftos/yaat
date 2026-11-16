using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The Scope toolbar's button model: one ordered list, each button with an id, a label and a kind.</summary>
public class ScopeToolbarButtonsTests
{
    private static IEnumerable<ScopeButton> Flatten(IEnumerable<ScopeButton> buttons) => buttons.SelectMany(b => Flatten(b.Children).Prepend(b));

    [Fact]
    public void TheBar_IsProfileTemplateRangeOthersVectorHistAutoMapsDbViewsBritePrefThenDelTearoff()
    {
        Assert.Equal(
            [
                ScopeButtonId.Profile,
                ScopeButtonId.Template,
                ScopeButtonId.Range,
                ScopeButtonId.Others,
                ScopeButtonId.Vector,
                ScopeButtonId.Hist,
                ScopeButtonId.Auto,
                ScopeButtonId.Maps,
                ScopeButtonId.Db,
                ScopeButtonId.Views,
                ScopeButtonId.Brite,
                ScopeButtonId.Pref,
                ScopeButtonId.DeleteTearoff,
            ],
            ScopeToolbarButtons.All.Select(b => b.Id)
        );
        Assert.Equal(
            ["PROFILE", "TEMPLATE", "RANGE", "OTHERS", "VECTOR", "HIST", "AUTO", "MAPS", "DB", "VIEWS", "BRITE", "PREF", "DEL TEAROFF"],
            ScopeToolbarButtons.All.Select(b => b.Label)
        );
    }

    [Fact]
    public void Profile_IsAMenuOfTheThreeProfiles_EachRunningItsScopeProfileCommand()
    {
        ScopeButton profile = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Profile);

        Assert.Equal(ScopeButtonKind.Menu, profile.Kind);
        Assert.Null(profile.Command);
        Assert.Equal([ScopeButtonId.ProfileStars, ScopeButtonId.ProfileEram, ScopeButtonId.ProfileMixed], profile.Children.Select(c => c.Id));
        Assert.Equal(["STARS", "ERAM", "MIXED"], profile.Children.Select(c => c.Label));
        Assert.Equal(["stars", "eram", "mixed"], profile.Children.Select(c => c.Value));
        Assert.Equal([".scope profile stars", ".scope profile eram", ".scope profile mixed"], profile.Children.Select(c => c.Command));
        Assert.All(profile.Children, c => Assert.Equal(ScopeButtonKind.Command, c.Kind));
    }

    [Fact]
    public void ProfileOrder_PutsTheNamedButtonsFirst_ThenTheRestInBarOrder_SkippingAnUnknownName()
    {
        List<string> unknown = [];

        IReadOnlyList<ScopeButton> ordered = ScopeProfilePicker.Order(
            ScopeToolbarButtons.All,
            ["MAPS", "range", "wx"],
            b => ScopeToolbarButtons.NameOf(ScopeToolbarButtons.KeyOf(b)),
            unknown
        );

        Assert.Equal(
            [
                ScopeButtonId.Maps,
                ScopeButtonId.Range,
                ScopeButtonId.Profile,
                ScopeButtonId.Template,
                ScopeButtonId.Others,
                ScopeButtonId.Vector,
                ScopeButtonId.Hist,
                ScopeButtonId.Auto,
                ScopeButtonId.Db,
                ScopeButtonId.Views,
                ScopeButtonId.Brite,
                ScopeButtonId.Pref,
                ScopeButtonId.DeleteTearoff,
            ],
            ordered.Select(b => b.Id)
        );
        Assert.Equal(["wx"], unknown);
    }

    [Fact]
    public void Brite_IsAMenuOfTheNineGroupSpinners_InOrder()
    {
        ScopeButton brite = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Brite);

        Assert.Equal(ScopeButtonKind.Menu, brite.Kind);
        Assert.Null(brite.Command);
        Assert.Equal(
            [
                ScopeButtonId.BriteMapA,
                ScopeButtonId.BriteMapB,
                ScopeButtonId.BriteTargets,
                ScopeButtonId.BriteFdb,
                ScopeButtonId.BriteLine4,
                ScopeButtonId.BriteAlerts,
                ScopeButtonId.BriteDwell,
                ScopeButtonId.BriteHist,
                ScopeButtonId.BriteVector,
            ],
            brite.Children.Select(c => c.Id)
        );
        Assert.Equal(["MAP A", "MAP B", "TARGETS", "FDB", "LINE 4", "ALERTS", "DWELL", "HIST", "VECTOR"], brite.Children.Select(c => c.Label));
        Assert.Equal(["mapa", "mapb", "targets", "fdb", "line4", "alerts", "dwell", "hist", "vector"], brite.Children.Select(c => c.Value));
        Assert.All(brite.Children, c => Assert.Equal(ScopeButtonKind.Spinner, c.Kind));
        Assert.All(brite.Children, c => Assert.Null(c.Command));
        Assert.All(brite.Children, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void Db_IsAMenuOfItsThreeToggles_ThenTheCharSpinner()
    {
        ScopeButton db = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Db);

        Assert.Equal(ScopeButtonKind.Menu, db.Kind);
        Assert.Null(db.Command);
        Assert.Equal([ScopeButtonId.Db4thLine, ScopeButtonId.DbDwell, ScopeButtonId.DbAtpa, ScopeButtonId.DbChar], db.Children.Select(c => c.Id));
        Assert.Equal(["4TH LINE", "DWELL", "ATPA DIST", "CHAR"], db.Children.Select(c => c.Label));
        Assert.Equal(
            [ScopeButtonKind.Toggle, ScopeButtonKind.Toggle, ScopeButtonKind.Toggle, ScopeButtonKind.Spinner],
            db.Children.Select(c => c.Kind)
        );
        Assert.Equal([".scope db 4thline", ".scope db dwell", ".scope db atpa", null], db.Children.Select(c => c.Command));
        Assert.Equal(".scope db char 16", ScopeToolbarButtons.DbCharCommand(16));
        Assert.All(db.Children, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void Maps_IsAMenu_WithNoStaticChildren()
    {
        ScopeButton maps = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Maps);

        Assert.Equal(ScopeButtonKind.Menu, maps.Kind);
        Assert.Null(maps.Command);
        Assert.Empty(maps.Children);
    }

    [Fact]
    public void MapButtons_ClearAllThenOneTogglePerMap_KeyedByStarsId()
    {
        ScopeButton clear = ScopeToolbarButtons.MapClear;
        Assert.Equal((ScopeButtonId.MapClear, "CLR ALL", ScopeButtonKind.Command), (clear.Id, clear.Label, clear.Kind));
        Assert.Null(clear.Value);
        Assert.Equal(".scope map clear", clear.Command);
        ScopeButton toggle = ScopeToolbarButtons.MapToggle(10, "SFO");
        Assert.Equal((ScopeButtonId.MapToggle, "10 SFO", ScopeButtonKind.Toggle), (toggle.Id, toggle.Label, toggle.Kind));
        Assert.Equal(("10", ".scope map 10"), (toggle.Value, toggle.Command));
        Assert.Empty(toggle.Children);
        Assert.Equal(".scope map 10 on", ScopeToolbarButtons.MapCommand(10, true));
        Assert.Equal(".scope map 10 off", ScopeToolbarButtons.MapCommand(10, false));
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

    /// <summary>
    /// The three judges agree: every id <see cref="ScopeToolbarButtons.CanTearOff"/> allows, named as
    /// <see cref="ScopeToolbarButtons.TryParseKey"/> parses it, resolves through <see cref="ScopeToolbarButtons.Find"/>;
    /// the map toggles, which carry a value, are reached by <c>ButtonFor</c> rather than <c>Find</c>.
    /// </summary>
    [Fact]
    public void EveryTearableId_ResolvesThroughFind()
    {
        ScopeButtonId[] tearable =
        [
            .. Enum.GetValues<ScopeButtonId>().Where(id => (id != ScopeButtonId.MapToggle) && ScopeToolbarButtons.CanTearOff(id)),
        ];

        Assert.NotEmpty(tearable);
        foreach (ScopeButtonId id in tearable)
        {
            string name = ScopeToolbarButtons.NameOf(new ScopeButtonKey(id, null));
            Assert.True(ScopeToolbarButtons.TryParseKey(name, out ScopeButtonKey? key), name);
            Assert.Equal(id, key.Id);

            ScopeButton? found = ScopeToolbarButtons.Find(key.Id);
            Assert.NotNull(found);
            Assert.Equal(id, found.Id);
            Assert.True(ScopeToolbarButtons.CanTearOff(found.Id), name);
        }
    }

    [Fact]
    public void EveryTemplatePick_CarriesItsScopeTemplateCommand()
    {
        ScopeButton template = ScopeToolbarButtons.All.Single(b => b.Id == ScopeButtonId.Template);

        Assert.Null(template.Command);
        Assert.All(template.Children, c => Assert.Equal($".scope template {c.Value}", c.Command));
    }
}

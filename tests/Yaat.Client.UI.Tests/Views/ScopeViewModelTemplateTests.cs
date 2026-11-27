using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's datablock template follows the room's profile alone: the STARS template until the room has a profile, then
/// the profile's own (STARS, ERAM, MIXED or EUROSCOPE).
/// </summary>
public class ScopeViewModelTemplateTests
{
    [AvaloniaTheory]
    [InlineData(ScopeProfileKind.Stars, ScopeTemplates.StarsId)]
    [InlineData(ScopeProfileKind.Eram, ScopeTemplates.EramId)]
    [InlineData(ScopeProfileKind.Mixed, ScopeTemplates.MixedId)]
    [InlineData(ScopeProfileKind.EuroScope, ScopeTemplates.EuroScopeId)]
    public void TemplateId_FollowsTheProfile(ScopeProfileKind kind, string expected)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        // Start from another profile, so the pick is a switch.
        main.PickScopeProfileByHand(kind == ScopeProfileKind.Eram ? ScopeProfileKind.Stars : ScopeProfileKind.Eram);
        List<string?> changed = [];
        void record(object? _, PropertyChangedEventArgs e) => changed.Add(e.PropertyName);
        scope.PropertyChanged += record;

        main.PickScopeProfileByHand(kind);

        scope.PropertyChanged -= record;
        Assert.Equal(expected, scope.TemplateId);
        Assert.Contains(nameof(ScopeViewModel.TemplateId), changed);
    }

    [AvaloniaFact]
    public void BeforeTheRoomHasAProfile_TheScopeRunsTheStarsProfile()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);

        Assert.Null(main.ScopeProfile);
        Assert.Equal(ScopeProfileKind.Stars, scope.ProfileKind);
        Assert.Equal(ScopeBarKind.Stars, scope.BarKind);
        Assert.Equal(ScopeTemplates.StarsId, scope.TemplateId);
    }
}

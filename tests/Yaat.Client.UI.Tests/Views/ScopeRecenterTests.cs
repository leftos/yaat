using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// DISPLAY's OFF CNTR: lit while the scope's centre is more than half a mile off the facility's home centre, and a press
/// pans back to that centre, keeping the range.
/// </summary>
public class ScopeRecenterTests
{
    private const double HomeLat = 37.62;
    private const double HomeLon = -122.38;

    // One minute of latitude is one nautical mile.
    private const double DegPerNm = 1.0 / 60.0;

    private static readonly ScopeButton OffCntr = ScopeToolbarButtons.Find(ScopeButtonId.DisplayOffCntr)!;

    private static (MainViewModel Main, ScopeViewModel Scope) SeededScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        ScopeMapToolsCommandTests.LoadSource(main, HomeLat, HomeLon);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        return (main, scope);
    }

    [AvaloniaFact]
    public void Centred_OffCntrIsNotLit()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            Assert.Equal((HomeLat, HomeLon), (scope.CenterLat, scope.CenterLon));
            Assert.False(scope.IsOffCenter);
            Assert.False(scope.IsActive(OffCntr));
        }
    }

    [AvaloniaFact]
    public void PointFourMilesOff_OffCntrIsNotLit()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.CenterLat = HomeLat + (0.4 * DegPerNm);

            Assert.False(scope.IsOffCenter);
            Assert.False(scope.IsActive(OffCntr));
        }
    }

    [AvaloniaFact]
    public void PointSixMilesOff_OffCntrIsLit()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.CenterLat = HomeLat + (0.6 * DegPerNm);

            Assert.True(scope.IsOffCenter);
            Assert.True(scope.IsActive(OffCntr));
        }
    }

    [AvaloniaFact]
    public void SourceRecentre_AtTheDefaultRangeNeverPanned_IsFollowed_OffCntrStaysDark()
    {
        (MainViewModel main, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            main.ScopeMaps.RecenterOnPrimaryAirport(HomeLat + DegPerNm, HomeLon);

            Assert.Equal((HomeLat + DegPerNm, HomeLon), (scope.CenterLat, scope.CenterLon));
            Assert.False(scope.IsOffCenter);
            Assert.False(scope.IsActive(OffCntr));
        }
    }

    [AvaloniaFact]
    public void SourceRecentre_MoreThanHalfAMileFromAPannedScope_LightsOffCntr()
    {
        (MainViewModel main, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.CenterLat = HomeLat + (0.1 * DegPerNm);
            List<string?> changes = RecordChanges(scope);

            main.ScopeMaps.RecenterOnPrimaryAirport(HomeLat + DegPerNm, HomeLon);

            Assert.Equal(HomeLat + (0.1 * DegPerNm), scope.CenterLat);
            Assert.True(scope.IsOffCenter);
            Assert.True(scope.IsActive(OffCntr));
            Assert.Equal([nameof(ScopeViewModel.IsOffCenter)], changes.Where(n => n == nameof(ScopeViewModel.IsOffCenter)));
        }
    }

    [AvaloniaFact]
    public void SourceRecentre_LessThanHalfAMileFromAPannedScope_LeavesOffCntrAsItWas()
    {
        (MainViewModel main, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.CenterLat = HomeLat + (0.1 * DegPerNm);
            List<string?> changes = RecordChanges(scope);

            main.ScopeMaps.RecenterOnPrimaryAirport(HomeLat + (0.3 * DegPerNm), HomeLon);

            Assert.False(scope.IsOffCenter);
            Assert.False(scope.IsActive(OffCntr));
            Assert.DoesNotContain(nameof(ScopeViewModel.IsOffCenter), changes);
        }
    }

    [AvaloniaFact]
    public void PanningInsideTheHalfMileBand_AnnouncesNoOffCntrChange()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            List<string?> changes = RecordChanges(scope);

            scope.CenterLat = HomeLat + (0.1 * DegPerNm);
            scope.CenterLon = HomeLon + (0.2 * DegPerNm);
            scope.CenterLat = HomeLat + (0.3 * DegPerNm);

            Assert.DoesNotContain(nameof(ScopeViewModel.IsOffCenter), changes);
        }
    }

    private static List<string?> RecordChanges(ScopeViewModel scope)
    {
        List<string?> changes = [];
        scope.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        return changes;
    }

    [AvaloniaFact]
    public void PressingOffCntr_ReturnsToTheHomeCentre_KeepingTheRange_AndUnlightsIt()
    {
        (MainViewModel main, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.CenterLat = HomeLat + (3 * DegPerNm);
            scope.CenterLon = HomeLon + (2 * DegPerNm);
            scope.RangeNm = 25;
            Assert.True(scope.IsOffCenter);

            scope.Press(OffCntr);

            Assert.Equal((HomeLat, HomeLon), (scope.CenterLat, scope.CenterLon));
            Assert.Equal(25, scope.RangeNm);
            Assert.False(scope.IsOffCenter);
            Assert.False(scope.IsActive(OffCntr));
            Assert.Equal("Radar centred", main.StatusText);
        }
    }
}

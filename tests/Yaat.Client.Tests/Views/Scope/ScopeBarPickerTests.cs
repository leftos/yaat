using Xunit;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The bar a Scope shows: picked from the room's Scope profile kind, Mixed and EuroScope naming today's bar.</summary>
public class ScopeBarPickerTests
{
    [Theory]
    [InlineData(ScopeProfileKind.Stars, ScopeBarKind.Stars)]
    [InlineData(ScopeProfileKind.Eram, ScopeBarKind.Eram)]
    [InlineData(ScopeProfileKind.Mixed, ScopeBarKind.Classic)]
    [InlineData(ScopeProfileKind.EuroScope, ScopeBarKind.Classic)]
    public void KindFor_MapsEachProfileKind(ScopeProfileKind kind, ScopeBarKind expected) => Assert.Equal(expected, ScopeBarPicker.KindFor(kind));
}

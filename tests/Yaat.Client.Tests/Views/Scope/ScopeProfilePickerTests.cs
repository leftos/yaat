using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The datablock template each Scope profile kind draws with: the mapping a profile applies the look through.</summary>
public class ScopeProfilePickerTests
{
    [Theory]
    [InlineData(ScopeProfileKind.Stars, ScopeTemplates.StarsId)]
    [InlineData(ScopeProfileKind.Eram, ScopeTemplates.EramId)]
    [InlineData(ScopeProfileKind.Mixed, ScopeTemplates.MixedId)]
    [InlineData(ScopeProfileKind.EuroScope, ScopeTemplates.EuroScopeId)]
    public void TemplateIdFor_MapsEachKind(ScopeProfileKind kind, string expected) => Assert.Equal(expected, ScopeProfilePicker.TemplateIdFor(kind));
}

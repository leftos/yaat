using Xunit;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>The apply dialog's window-geometry rows name each window a layout captured, the Scope windows included.</summary>
public class ApplyLayoutDialogLabelTests
{
    [Theory]
    [InlineData("ScopeView", "Radar window")]
    [InlineData("ScopeView#2", "Radar window #2")]
    [InlineData("ScopeView#3", "Radar window #3")]
    public void FriendlyWindowName_NamesTheScopeWindows(string key, string expected) =>
        Assert.Equal(expected, ApplyLayoutDialog.FriendlyWindowName(key));
}

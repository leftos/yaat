using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the GPU renderer-backend preference (macOS Metal/OpenGL/Software override). UserPreferences writes to
// this test's own preferences file; a fresh instance proves the disk round-trip.
public class UserPreferencesRendererModeTests
{
    [Fact]
    public void Default_IsAuto() => Assert.Equal(RendererMode.Auto, new UserPreferences().RendererMode);

    [Fact]
    public void SetRendererMode_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetRendererMode(RendererMode.OpenGl);
        Assert.Equal(RendererMode.OpenGl, prefs.RendererMode);
        Assert.Equal(RendererMode.OpenGl, new UserPreferences().RendererMode);

        prefs.SetRendererMode(RendererMode.Software);
        Assert.Equal(RendererMode.Software, new UserPreferences().RendererMode);
    }
}

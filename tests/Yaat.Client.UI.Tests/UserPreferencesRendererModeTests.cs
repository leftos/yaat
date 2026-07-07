using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the GPU renderer-backend preference (macOS Metal/OpenGL/Software override). UserPreferences
// writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process temp dir; a fresh
// instance proves the disk round-trip. The mutating test restores the default so the Defaults test
// stays order-tolerant against the shared preferences.json.
public class UserPreferencesRendererModeTests
{
    [Fact]
    public void Default_IsAuto()
    {
        Assert.Equal(RendererMode.Auto, new UserPreferences().RendererMode);
    }

    [Fact]
    public void SetRendererMode_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetRendererMode(RendererMode.OpenGl);
        Assert.Equal(RendererMode.OpenGl, prefs.RendererMode);
        Assert.Equal(RendererMode.OpenGl, new UserPreferences().RendererMode);

        prefs.SetRendererMode(RendererMode.Software);
        Assert.Equal(RendererMode.Software, new UserPreferences().RendererMode);

        // Restore the default so the Defaults test is order-independent.
        prefs.SetRendererMode(RendererMode.Auto);
        Assert.Equal(RendererMode.Auto, new UserPreferences().RendererMode);
    }
}

using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// The saved Radar settings record, kept so old preference files still load, starts with no pinned markers.
public class UserPreferencesPinnedMarkersTests
{
    [Fact]
    public void RadarSettings_NoSavedValue_PinnedMarkersDefaultEmpty()
    {
        var saved = new SavedRadarSettings();
        Assert.Empty(saved.PinnedMarkers);
    }
}

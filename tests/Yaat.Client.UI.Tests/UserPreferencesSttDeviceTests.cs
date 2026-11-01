using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the speech-recognition device preference. UserPreferences writes to this test's own preferences file; a fresh
// instance proves the disk round-trip.
public class UserPreferencesSttDeviceTests
{
    [Fact]
    public void Default_IsCpu() => Assert.Equal(SttDevice.Cpu, new UserPreferences().SttDevice);

    [Fact]
    public void SetSttDevice_Gpu_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetSttDevice(SttDevice.Gpu);

        Assert.Equal(SttDevice.Gpu, prefs.SttDevice);
        Assert.Equal(SttDevice.Gpu, new UserPreferences().SttDevice);
    }

    [Fact]
    public void Unknown_Stored_Value_Reads_As_Cpu()
    {
        File.WriteAllText(UserPreferences.FilePath, """{ "sttDevice": "Tpu" }""");

        Assert.Equal(SttDevice.Cpu, new UserPreferences().SttDevice);
    }
}

using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests;

public sealed class FaaAircraftDataServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FaaAircraftDataServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yaat-faa-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        // Reset lookup
        AircraftApproachSpeed.Initialize(new Dictionary<string, int>());
    }

    public void Dispose()
    {
        AircraftApproachSpeed.Initialize(new Dictionary<string, int>());

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void JsonRoundTrip_LoadsCorrectly()
    {
        // Write test JSON
        var data = new Dictionary<string, int>
        {
            ["B738"] = 144,
            ["A320"] = 136,
            ["C172"] = 62,
        };
        var json = JsonSerializer.Serialize(data);
        var path = Path.Combine(_tempDir, "test.json");
        File.WriteAllText(path, json);

        // Load and verify
        var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
        Assert.NotNull(loaded);

        AircraftApproachSpeed.Initialize(loaded);

        Assert.Equal(144, AircraftApproachSpeed.GetApproachSpeed("B738"));
        Assert.Equal(136, AircraftApproachSpeed.GetApproachSpeed("A320"));
        Assert.Equal(62, AircraftApproachSpeed.GetApproachSpeed("C172"));
    }

    [Fact]
    public void NoCacheAvailable_CategoryDefaultsUsed()
    {
        // Without initialization, approach speed returns null → category default
        double speed = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet, "B738");
        Assert.Equal(CategoryPerformance.ApproachSpeed(AircraftCategory.Jet), speed);
    }
}

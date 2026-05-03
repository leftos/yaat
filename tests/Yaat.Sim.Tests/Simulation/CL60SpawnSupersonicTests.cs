using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the airborne-spawn IAS bug. KFB7 (a CL60 / Bombardier Challenger 600)
/// in scenario "S3-NCTC-3 | Area C Complete" spawns at MYJAW at FL280 with
/// IndicatedAirspeed = 460. At FL280 that resolves to TAS ~748 / Mach ~1.11 — supersonic.
///
/// Root cause: <see cref="AircraftPerformance.DefaultSpeed"/> returns the profile's
/// CruiseSpeed (TAS in knots, or Mach when &lt; 1.0) directly without converting
/// to IAS at the spawn altitude. ScenarioLoader and AircraftGenerator then assign
/// the result straight into IndicatedAirspeed.
///
/// Recording: cl60-spawn-supersonic-recording.yaat-bug-report-bundle.zip
/// </summary>
public class CL60SpawnSupersonicTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/cl60-spawn-supersonic-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Direct unit test on the function: a CL60 at FL280 with no target altitude
    /// (level cruise) must return a sensible KIAS, not the profile's raw TAS.
    /// CL60 cruise IAS at FL280 ≈ 280-285 KIAS (TAS 460 → IAS via ISA).
    /// </summary>
    [Fact]
    public void DefaultSpeed_CL60_AtFL280_ReturnsIasNotTas()
    {
        TestVnasData.EnsureInitialized();
        var category = AircraftCategorization.Categorize("CL60");

        double speed = AircraftPerformance.DefaultSpeed("CL60", category, 28000, targetAltitude: null);

        output.WriteLine($"DefaultSpeed(CL60, FL280, level) = {speed:F1}");
        Assert.True(
            speed is > 240 and < 320,
            $"CL60 at FL280 in level cruise should resolve to ~280 KIAS, got {speed:F1}. "
                + "TAS=460 in profile must be converted, not returned as IAS."
        );
    }

    /// <summary>
    /// Sea-level sanity: the &lt; 10000 ft IAS cap of 250 KIAS still applies
    /// after the unit conversion, so a jet "cruising" at sea level is clamped.
    /// </summary>
    [Fact]
    public void DefaultSpeed_CL60_AtSeaLevel_RespectsSpeedLimit()
    {
        TestVnasData.EnsureInitialized();
        var category = AircraftCategorization.Categorize("CL60");

        double speed = AircraftPerformance.DefaultSpeed("CL60", category, 0, targetAltitude: null);

        output.WriteLine($"DefaultSpeed(CL60, SL, level) = {speed:F1}");
        Assert.True(speed <= 250.5, $"Below 10k the 250 KIAS limit must apply (CL60 has IsSpeedLimitWaived=false), got {speed:F1}");
    }

    /// <summary>
    /// E2E: replay the recording to t=0 and check KFB7's spawn IAS through the
    /// full ScenarioLoader → AircraftPerformance → IndicatedAirspeed pipeline.
    /// </summary>
    [Fact]
    public void KFB7_SpawnsSubsonic()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 0);

        var aircraft = engine.FindAircraft("KFB7");
        Assert.NotNull(aircraft);

        output.WriteLine($"KFB7 at spawn: alt={aircraft.Altitude:F0} IAS={aircraft.IndicatedAirspeed:F1} type={aircraft.AircraftType}");

        Assert.Equal(28000, aircraft.Altitude, 1);
        Assert.True(
            aircraft.IndicatedAirspeed is > 200 and < 320,
            $"CL60 spawning at FL280 should have IAS in the 250-300 KIAS range, got {aircraft.IndicatedAirspeed:F1}. "
                + "460 indicates the profile's cruise TAS was assigned directly without conversion."
        );
    }
}

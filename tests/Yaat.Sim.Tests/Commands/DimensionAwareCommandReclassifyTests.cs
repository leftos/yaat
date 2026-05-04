using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Audit follow-up: <c>RNS</c> / <c>DSR</c> are speed-axis commands (resume normal speed,
/// delete speed restrictions); <c>FPH</c> is a heading-axis command (snap to present
/// heading). All three were classified as <c>TrackedCommandType.Immediate</c>, mapping
/// to <c>CommandDimension.None</c> — which dropped them into the fast-path queue-wipe
/// instead of competing only with same-dimension queued blocks. Reclassifying them so
/// they preserve unrelated queued instructions (lateral vs vertical vs speed) and only
/// drop blocks they actually conflict with.
/// </summary>
public class DimensionAwareCommandReclassifyTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public DimensionAwareCommandReclassifyTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    private static AircraftState MakeAirborne() =>
        new()
        {
            Callsign = "N435C",
            AircraftType = "M20P",
            Position = new LatLon(37.62, -122.19),
            TrueHeading = new TrueHeading(340),
            Altitude = 1500,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KOAK",
            },
        };

    private static DispatchContext Ctx() => TestDispatch.Context(Random.Shared, validateDctFixes: false);

    private static void DispatchOk(AircraftState ac, string text)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx());
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Rns_DropsQueuedSpeed_PreservesQueuedNavigation()
    {
        var ac = MakeAirborne();

        DispatchOk(ac, "DCT OAK; SPD 100");
        Assert.Equal(2, ac.Queue.Blocks.Count);

        DispatchOk(ac, "RNS");

        // RNS (Speed) conflicts with the pending SPD block but not with DCT OAK
        // (Lateral / current applied block).
        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("OAK"));
        Assert.DoesNotContain(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Speed 100"));
    }

    [Fact]
    public void Dsr_DropsQueuedSpeed_PreservesQueuedNavigation()
    {
        var ac = MakeAirborne();

        DispatchOk(ac, "DCT OAK; SPD 100");
        Assert.Equal(2, ac.Queue.Blocks.Count);

        DispatchOk(ac, "DSR");

        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("OAK"));
        Assert.DoesNotContain(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Speed 100"));
    }

    [Fact]
    public void Fph_DropsQueuedNavigation_PreservesQueuedAltitudeAndSpeed()
    {
        var ac = MakeAirborne();
        ac.Targets.TargetAltitude = 5000; // so AT-altitude validation passes

        // Queue: CM 050 (Vertical), SPD 100 (Speed), DCT OAK (Lateral).
        DispatchOk(ac, "CM 050; SPD 100; DCT OAK");
        Assert.Equal(3, ac.Queue.Blocks.Count);

        DispatchOk(ac, "FPH");

        // FPH (Heading/Lateral) drops the lateral block but leaves altitude/speed alone.
        Assert.DoesNotContain(ac.Queue.Blocks, b => b.NaturalDescription.Contains("OAK"));
        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Speed 100"));
    }
}

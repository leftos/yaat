using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Empirical tick cost measurements to guide sub-second tick rate decisions.
/// Not a formal benchmark (no warmup/statistics) — just a quick measurement.
/// </summary>
public class TickBenchmarkTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    public void MeasureTickCost(int aircraftCount)
    {
        var world = new SimulationWorld();
        for (int i = 0; i < aircraftCount; i++)
        {
            var ac = MakeAircraft($"TEST{i}", i);
            world.AddAircraft(ac);
        }

        // Warmup
        for (int i = 0; i < 10; i++)
        {
            world.Tick(1.0);
        }

        // Measure
        const int iterations = 1000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            world.Tick(0.25);
        }

        sw.Stop();

        double totalMs = sw.Elapsed.TotalMilliseconds;
        double perTickMs = totalMs / iterations;
        double perAircraftUs = (totalMs / iterations / aircraftCount) * 1000;

        _output.WriteLine(
            $"Aircraft={aircraftCount}: {perTickMs:F3}ms/tick, {perAircraftUs:F1}us/aircraft, {totalMs:F0}ms total for {iterations} ticks"
        );

        // At 4 sub-ticks per wall-clock second at 16x sim rate = 64 ticks/s,
        // we need each tick under ~15ms to stay within budget.
        // This is just informational — no assertion on performance.
    }

    [Fact]
    public void MeasureTickWithPhases()
    {
        var world = new SimulationWorld();
        for (int i = 0; i < 20; i++)
        {
            var ac = MakeAircraftWithPhases($"PHASE{i}", i);
            world.AddAircraft(ac);
        }

        // Warmup
        for (int i = 0; i < 10; i++)
        {
            world.Tick(1.0);
        }

        const int iterations = 1000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            world.Tick(0.25);
        }

        sw.Stop();

        double perTickMs = sw.Elapsed.TotalMilliseconds / iterations;
        _output.WriteLine($"20 aircraft with phases: {perTickMs:F3}ms/tick");
    }

    private static AircraftState MakeAircraft(string callsign, int index)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Latitude = 37.7 + index * 0.01,
            Longitude = -122.2 + index * 0.01,
            Heading = 280,
            Altitude = 5000 + index * 100,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };
    }

    private static AircraftState MakeAircraftWithPhases(string callsign, int index)
    {
        var ac = MakeAircraft(callsign, index);
        ac.Phases = new PhaseList();
        ac.Targets.TargetHeading = 350;
        ac.Targets.TargetAltitude = 8000;
        ac.Targets.TargetSpeed = 300;
        return ac;
    }
}

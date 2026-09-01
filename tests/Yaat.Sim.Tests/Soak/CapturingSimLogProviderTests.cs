using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Soak;

namespace Yaat.Sim.Tests.Soak;

/// <summary>
/// <see cref="CapturingSimLogProvider"/> — the soak harness's Warning+ log tap: a bounded ring buffer any
/// <see cref="ILoggerFactory"/> can host, drained once per tick by the runner.
/// </summary>
public class CapturingSimLogProviderTests
{
    [Fact]
    public void CapturesWarningAndAbove_ThroughSimLog_AndDrainClears()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);
        var log = SimLog.CreateLogger("TapTest");

        log.LogInformation("info is below the floor");
        log.LogWarning("warn {N}", 1);
        log.LogError(new InvalidOperationException("boom"), "err {N}", 2);

        var records = tap.Drain();
        Assert.Equal(2, records.Count);
        Assert.Equal(LogLevel.Warning, records[0].Level);
        Assert.Equal("TapTest", records[0].Category);
        Assert.Equal("warn 1", records[0].Message);
        Assert.Null(records[0].ExceptionText);
        Assert.Equal(LogLevel.Error, records[1].Level);
        Assert.Equal("err 2", records[1].Message);
        Assert.Contains("boom", records[1].ExceptionText);
        Assert.Empty(tap.Drain());
    }

    [Fact]
    public void RingOverflow_DropsOldest_AndCounts()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 3);
        var log = tap.CreateLogger("Ring");

        for (int i = 0; i < 5; i++)
        {
            log.LogWarning("w{I}", i);
        }

        var records = tap.Drain();
        Assert.Equal(["w2", "w3", "w4"], records.Select(r => r.Message).ToArray());
        Assert.Equal(2, tap.DroppedCount);
    }

    [Fact]
    public void Drain_IsThreadSafe()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 1_000);
        var log = tap.CreateLogger("Parallel");
        const int writers = 8;
        const int perWriter = 500;

        Parallel.For(
            0,
            writers,
            _ =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    log.LogWarning("p{I}", i);
                }
            }
        );

        var captured = tap.Drain().Count;
        Assert.Equal(writers * perWriter, captured + tap.DroppedCount);
        Assert.True(captured <= 1_000);
    }
}

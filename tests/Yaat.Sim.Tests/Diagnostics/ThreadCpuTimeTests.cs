using System.Diagnostics;
using Xunit;
using Yaat.Sim.Diagnostics;

namespace Yaat.Sim.Tests.Diagnostics;

public class ThreadCpuTimeTests(ITestOutputHelper output)
{
    /// <summary>
    /// The most CPU time a sleeping thread may be charged: Windows charges a thread's CPU time in whole scheduler ticks
    /// (about 15.6 ms), so a thread that happens to be running at a tick around the sleep can be charged one or two.
    /// </summary>
    private static readonly TimeSpan SleepTolerance = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void BusyLoop_GrowsThreadCpuTime()
    {
        var target = TimeSpan.FromMilliseconds(100);
        var hangGuard = TimeSpan.FromSeconds(20);
        TimeSpan start = ThreadCpuTime.Current();
        var wall = Stopwatch.StartNew();
        TimeSpan spent = TimeSpan.Zero;
        long spins = 0;
        while ((spent < target) && (wall.Elapsed < hangGuard))
        {
            spins++;
            spent = ThreadCpuTime.Current() - start;
        }

        output.WriteLine($"{spins:N0} spins: {spent.TotalMilliseconds:F1} ms of thread CPU time in {wall.Elapsed.TotalMilliseconds:F0} ms wall");
        Assert.True(spent >= target, $"a busy loop gained only {spent.TotalMilliseconds:F1} ms of thread CPU time in {hangGuard.TotalSeconds:F0} s");
    }

    [Fact]
    public void Sleep_AddsNearZeroCpuTime_WhileAnotherThreadSpins()
    {
        var hangGuard = TimeSpan.FromSeconds(20);
        using var stop = new ManualResetEventSlim(false);
        using var spunPastTolerance = new ManualResetEvent(false);
        TimeSpan spun = TimeSpan.Zero;
        var spinner = new Thread(() =>
        {
            TimeSpan start = ThreadCpuTime.Current();
            while (!stop.IsSet)
            {
                Thread.SpinWait(1000);
                spun = ThreadCpuTime.Current() - start;
                if (spun > SleepTolerance)
                {
                    spunPastTolerance.Set();
                }
            }
        })
        {
            IsBackground = true,
        };
        spinner.Start();

        TimeSpan asleep;
        try
        {
            // Sleep at least 200 ms, and on until the spinner has burnt more CPU than the sleeper may be charged; a kernel event,
            // not a spinning wait, so the waiting itself costs the sleeper nothing.
            asleep = ThreadCpuTime.Measure(() =>
            {
                Thread.Sleep(200);
                spunPastTolerance.WaitOne(hangGuard);
            });
        }
        finally
        {
            stop.Set();
            spinner.Join();
        }

        output.WriteLine(
            $"the spinner used {spun.TotalMilliseconds:F1} ms of thread CPU time while the sleeper was charged {asleep.TotalMilliseconds:F1} ms"
        );
        Assert.True(
            spun > SleepTolerance,
            $"the spinner used only {spun.TotalMilliseconds:F1} ms of thread CPU time in {hangGuard.TotalSeconds:F0} s, "
                + $"not more than the {SleepTolerance.TotalMilliseconds:F0} ms tolerance, so the test proves nothing"
        );
        Assert.True(asleep >= TimeSpan.Zero, $"sleeping went backwards: {asleep.TotalMilliseconds:F1} ms of thread CPU time");
        Assert.True(
            asleep <= SleepTolerance,
            $"Thread.Sleep(200) was charged {asleep.TotalMilliseconds:F1} ms of thread CPU time while another thread spun; "
                + "the clock is not per-thread"
        );
    }

    [Fact]
    public void Measure_NullAction_Throws()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ThreadCpuTime.Measure(null!));

        Assert.Equal("action", ex.ParamName);
    }
}

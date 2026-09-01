using Xunit;
using Yaat.Sim.ControllerAi;

namespace Yaat.Sim.Tests.ControllerAi;

public class AiAnomalyLogTests
{
    [Fact]
    public void OpenCloseLifecycle_EmitsOrderedEventsWithDuration()
    {
        var log = new AiAnomalyLog();

        log.Open(AiAnomalyKind.StuckAircraft, "gnd", "N1", 10, "no movement");
        log.Open(AiAnomalyKind.StuckAircraft, "gnd", "N1", 20, "duplicate open is ignored");
        Assert.True(log.IsOpen(AiAnomalyKind.StuckAircraft, "gnd", "N1"));
        Assert.Equal(1, log.OpenCount);

        log.Close(AiAnomalyKind.StuckAircraft, "gnd", "N1", 70);
        log.Close(AiAnomalyKind.StuckAircraft, "gnd", "N1", 80);
        Assert.False(log.IsOpen(AiAnomalyKind.StuckAircraft, "gnd", "N1"));

        var events = log.Drain();
        Assert.Equal(2, events.Count);
        Assert.Equal(AiAnomalyEventKind.Opened, events[0].Event);
        Assert.Equal(10, events[0].AtSeconds);
        Assert.Equal("no movement", events[0].Detail);
        Assert.Equal(AiAnomalyEventKind.Closed, events[1].Event);
        Assert.Equal(60, events[1].DurationSeconds);
        Assert.Empty(log.Drain());
    }

    [Fact]
    public void Record_IsAPointEvent_AndClearDropsEverything()
    {
        var log = new AiAnomalyLog();

        log.Record(AiAnomalyKind.CommandRejected, "gnd", "N1", 5, "CTO: aircraft is parked");
        log.Open(AiAnomalyKind.UnansweredPilotRequest, "gnd", "N2", 6, "taxi");

        var events = log.Drain();
        Assert.Equal(AiAnomalyEventKind.Instant, events[0].Event);
        Assert.Equal(0, events[0].DurationSeconds);

        log.Clear();
        Assert.Equal(0, log.OpenCount);
        Assert.Empty(log.Drain());
    }

    [Fact]
    public void OpenSubjects_AreScopedToKindAndPosition_AndOrdinallySorted()
    {
        var log = new AiAnomalyLog();
        log.Open(AiAnomalyKind.StuckAircraft, "gnd", "N9", 1, "");
        log.Open(AiAnomalyKind.StuckAircraft, "gnd", "N1", 1, "");
        log.Open(AiAnomalyKind.StuckAircraft, "twr", "N5", 1, "");
        log.Open(AiAnomalyKind.UnansweredPilotRequest, "gnd", "N0", 1, "");

        Assert.Equal(["N1", "N9"], log.OpenSubjects(AiAnomalyKind.StuckAircraft, "gnd"));
        Assert.Equal(["N5"], log.OpenSubjects(AiAnomalyKind.StuckAircraft, "twr"));
    }
}

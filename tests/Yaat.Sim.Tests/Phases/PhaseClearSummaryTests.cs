using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// <see cref="PhaseClearSummary"/> labels what a dispatcher-side phase clear cancelled.
/// A bare instrument final (<c>[FinalApproach, Landing]</c>, no VFR circuit) must read as
/// an "approach", not a "pattern" — the SWA4587 OAK ILS 30 warning said "pattern to RWY 30
/// cancelled by RFAS", which is doubly wrong (it's an approach, and RFAS shouldn't clear it).
/// A genuine VFR circuit (a real pattern leg ahead, or <c>TrafficDirection</c> set) still
/// reads as a "pattern".
/// </summary>
public class PhaseClearSummaryTests
{
    private static PhaseList InstrumentFinal() => new() { AssignedRunway = TestRunwayFactory.Make(designator: "30") };

    [Fact]
    public void InstrumentFinal_IsApproachNotPattern()
    {
        PhaseList phases = InstrumentFinal();
        phases.Add(new FinalApproachPhase());
        phases.Add(new LandingPhase());

        string? summary = PhaseClearSummary.Build(phases);

        Assert.Equal("approach to RWY 30", summary);
    }

    [Fact]
    public void PatternWithCircuitLeg_IsPattern()
    {
        PhaseList phases = InstrumentFinal();
        phases.Add(new DownwindPhase());
        phases.Add(new BasePhase());
        phases.Add(new FinalApproachPhase());
        phases.Add(new LandingPhase());

        string? summary = PhaseClearSummary.Build(phases);

        Assert.Equal("pattern to RWY 30", summary);
    }

    [Fact]
    public void FinalWithTrafficDirection_IsPattern()
    {
        PhaseList phases = InstrumentFinal();
        phases.TrafficDirection = PatternDirection.Left;
        phases.Add(new FinalApproachPhase());
        phases.Add(new LandingPhase());

        string? summary = PhaseClearSummary.Build(phases);

        Assert.Equal("pattern to RWY 30", summary);
    }
}

using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetLiveCheckFixtures;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The live check of OAK gates 25 and 27's stored <c>PUSH TE</c> against a B738 on the next gate or on the push line
/// (issues #222 and #475).
/// </summary>
public class PushTargetLiveCheckRowTests
{
    /// <summary>The targets of OAK gates 25 and 27, the stands the row tests push from.</summary>
    private static readonly Lazy<IReadOnlyList<PushTargetEntry>> OakRowEntries = new(() =>
        PushTargetPlanner.ComputeStands(
            PushTargetPlannerTests.Oak(),
            Envelopes.Value,
            PushTargetPlannerTests.Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "25", "27" },
            PushTargetPlannerTests.Sequential
        )
    );

    /// <summary>Issue #475: a B738 on gate 27 pushed onto TE passes the B738 on staggered gate 29 at the row's wingtip gap.</summary>
    [Fact]
    public void Oak_Gate27_PushTe_NeighbourOnStaggeredGate29_NotBlocked()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PushbackPose start = StartOf(Stand(layout, "27"));

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(
            Target(OakRowEntries.Value, "27", GroupOf(Narrowbody), "TE"),
            Request(
                Parked(Subject, Narrowbody, start, "27"),
                AircraftFootprint.FromType(Narrowbody),
                [Parked(Neighbour, Narrowbody, StartOf(Stand(layout, "29")), "29")]
            )
        );

        Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
    }

    /// <summary>Issue #475: a B738 parked on gate 27's push line, 230 ft back, blocks <c>PUSH TE</c>.</summary>
    [Fact]
    public void Oak_Gate27_PushTe_NeighbourOnThePushLine_Blocked()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PushbackPose start = StartOf(Stand(layout, "27"));
        var onPushLine = new PushbackPose(Offset(start, 180.0, PushLineNeighbourAftFt), start.NoseTrueDeg);

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(
            Target(OakRowEntries.Value, "27", GroupOf(Narrowbody), "TE"),
            Request(
                Parked(Subject, Narrowbody, start, "27"),
                AircraftFootprint.FromType(Narrowbody),
                [Parked(Neighbour, Narrowbody, onPushLine, null)]
            )
        );

        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), verdict);
    }

    /// <summary>Issue #222: a B738 on gate 25 pushed onto TE passes the B738 parked on gate 26.</summary>
    [Fact]
    public void Oak_Gate25_PushTe_B738OnGate26_NotBlocked()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PushbackPose start = StartOf(Stand(layout, "25"));

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(
            Target(OakRowEntries.Value, "25", GroupOf(Narrowbody), "TE"),
            Request(
                Parked(Subject, Narrowbody, start, "25"),
                AircraftFootprint.FromType(Narrowbody),
                [Parked(Neighbour, Narrowbody, StartOf(Stand(layout, "26")), "26")]
            )
        );

        Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
    }
}

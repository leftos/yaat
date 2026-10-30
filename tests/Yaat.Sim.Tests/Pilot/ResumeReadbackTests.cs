using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The pilot readback for RES must voice the CROSS / HS modifiers, not just "resume taxi".
/// <c>RES CROSS 28L</c> parses into one <see cref="ResumeCommand"/> whose <c>CrossRunways</c>
/// were silently dropped by the verbalizer (no <c>ResumeCommand</c> arm in <c>ExtractArgs</c>,
/// only zero-capture "resume taxi" rules). Mirrors the TAXI cross/hold-short readback forms
/// (see <see cref="Issue172TaxiReadbackTests"/>).
/// </summary>
public class ResumeReadbackTests
{
    public ResumeReadbackTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Res_Bare_VoicesResumeTaxi()
    {
        Assert.Equal("resume taxi", PhraseologyVerbalizer.Verbalize(new ResumeCommand([], [])));
        Assert.Equal("resume taxi", PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand([], [])));
    }

    [Fact]
    public void Res_CrossRunway_Spoken_VoicesCrossing()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ResumeCommand(["28L"], []));
        Assert.Equal("resume taxi, cross runway two eight left", result);
    }

    [Fact]
    public void Res_CrossRunway_Terminal_VoicesCrossing()
    {
        string? result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand(["28L"], []));
        Assert.Equal("resume taxi, cross runway 28L", result);
    }

    [Fact]
    public void Res_MultipleCrossings_JoinedWithAnd()
    {
        string? result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand(["28R", "28L"], []));
        Assert.Equal("resume taxi, cross runway 28R and 28L", result);
    }

    [Fact]
    public void Res_HoldShortRunway_VoicesHoldShort()
    {
        string? result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand([], [HoldShortTarget.Parse("28R")]));
        Assert.Equal("resume taxi, hold short of runway 28R", result);
    }

    [Fact]
    public void Res_CrossAndHoldShort_VoicesBothClauses()
    {
        string? result = PhraseologyVerbalizer.VerbalizeTerminal(new ResumeCommand(["28L"], [HoldShortTarget.Parse("28R")]));
        Assert.Equal("resume taxi, cross runway 28L, hold short of runway 28R", result);
    }

    /// <summary>
    /// A bare RES that pre-clears the taxiway hold short ahead of a rolling aircraft releases that bar, which the pilot reads
    /// back as the §3-7 release, "continue taxi" — not "resume taxi", which answers a hold — and the queued route goes on along
    /// A past the bar, so the legs past it follow (the route knows no clearance taxiways, so every leg is read).
    /// </summary>
    [Fact]
    public void Res_Bare_PreClearingTaxiwayHoldShort_ReadsBackContinueTaxi()
    {
        AircraftState ac = MakeTaxiingAircraftHoldingShortOfBAhead();
        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [])])]);

        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(compound, result, ac, PilotSpeechContext.Canonical, ActiveRunways.Empty);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(readback);
        Assert.Equal("continue taxi via A", readback.Terminal);
        Assert.StartsWith("continue taxi via alpha", readback.Tts);
    }

    /// <summary>
    /// A B738 taxiing along A (nodes 1 → 2 → 3), neither held nor capped by the detector, told to hold short of taxiway B at
    /// node 2.
    /// </summary>
    private static AircraftState MakeTaxiingAircraftHoldingShortOfBAhead()
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Phases = new PhaseList(),
        };
        ac.Phases.Add(new TaxiingPhase());
        ac.Phases.Start(
            new PhaseContext
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 0,
                Logger = NullLogger.Instance,
            }
        );
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [Segment(1, 2), Segment(2, 3)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                },
            ],
        };
        return ac;
    }

    private static TaxiRouteSegment Segment(int fromId, int toId)
    {
        var from = new GroundNode
        {
            Id = fromId,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var to = new GroundNode
        {
            Id = toId,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [from, to],
            TaxiwayName = "A",
            DistanceNm = 0.1,
        };
        return new TaxiRouteSegment { TaxiwayName = "A", Edge = edge.Directed(from, to) };
    }
}

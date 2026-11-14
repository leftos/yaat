using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Helpers.FollowTestHelpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The call a free pursuit makes when a spacing excursion starts names what the follower flies: "extending downwind for
/// spacing" while it is extending the lead's pre-base leg along the downwind (within 30° of the circuit's downwind heading),
/// "S-turning for spacing" for every other excursion, the two sharing one cooldown. Real OAK navdata, a B738 lead on its
/// right base to 28R and a C172 follower.
/// </summary>
public class FollowExcursionCallTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";
    private const string ExtendingDownwindCall = "extending downwind for spacing behind LEAD1.";
    private const string STurnCall = "S-turning for spacing behind LEAD1.";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("VfrFollowPhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    /// <summary>The lead's base start: a wide right base to 28R, 4 nm out and 2.5 nm right of the centerline.</summary>
    private static LatLon LeadBaseStart => OffFinal(Oak("28R"), 4.0, 2.5);

    /// <summary>A jet lead on its right base to 28R (3 nm pattern spacing behind it), its base started at <see cref="LeadBaseStart"/>.</summary>
    private static AircraftState AddJetBaseLead(SimulationEngine engine) => AddJetBaseLead(engine, LeadBaseStart);

    /// <summary>A jet lead on its right base to 28R (3 nm pattern spacing behind it), its base started at <paramref name="baseStart"/>.</summary>
    private static AircraftState AddJetBaseLead(SimulationEngine engine, LatLon baseStart)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, baseStart, rwy.TrueHeading - 90.0, 1500);
        lead.AircraftType = "B738";
        engine.World.AddAircraft(lead);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = rwy,
                Category = AircraftCategory.Jet,
                AircraftType = lead.AircraftType,
                WindSpeedKt = 0,
                Direction = PatternDirection.Right,
                EntryLeg = PatternEntryLeg.Base,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = NavigationDatabase.Instance.GetRunways(rwy.AirportId),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        lead.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        foreach (Phase phase in circuit)
        {
            lead.Phases.Add(phase);
        }

        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead, ActiveRunways.Empty));
        Assert.IsType<BasePhase>(lead.Phases.CurrentPhase);
        return lead;
    }

    /// <summary>A follower already in free pursuit of <see cref="Leader"/> (no pattern return), at <paramref name="position"/>.</summary>
    private static AircraftState AddPursuingFollower(SimulationEngine engine, LatLon position, TrueHeading heading)
    {
        AircraftState follower = MakeVfr(Follower, position, heading, 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(follower, Leader, patternReturn: null, climbOutGate: null);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    private static int Calls(AircraftState follower, string call) => follower.PendingWarnings.Count(w => w.Contains(call, StringComparison.Ordinal));

    /// <summary>
    /// Extending the jet lead's downwind 0.5 nm past its base start, short of the jet's 3 nm spacing: the excursion's call is
    /// "extending downwind for spacing", once, and no "S-turning" follows within 60 s.
    /// </summary>
    [Fact]
    public void ExcursionWhileExtendingTheDownwindPastTheLeadsBaseStart_CallsExtendingDownwind()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddJetBaseLead(engine);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;
        AircraftState follower = AddPursuingFollower(engine, GeoMath.ProjectPoint(LeadBaseStart, downwind, 0.5), downwind);

        TickSeconds(engine, 60);
        output.WriteLine(string.Join(Environment.NewLine, follower.PendingWarnings));

        Assert.Equal(1, Calls(follower, ExtendingDownwindCall));
        Assert.Equal(0, Calls(follower, "S-turning"));
    }

    /// <summary>
    /// Short of the lead's base start on its downwind line, too close behind it: the excursion is an S-turn, as before. The
    /// base starts 7 nm out so the follower is beyond the turn-out's range, which would otherwise turn it out first.
    /// </summary>
    [Fact]
    public void ExcursionBeforeTheLeadsBaseStart_CallsSTurning()
    {
        SimulationEngine engine = BuildEngine();
        LatLon baseStart = OffFinal(Oak("28R"), 7.0, 2.5);
        AircraftState lead = AddJetBaseLead(engine, baseStart);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;
        AircraftState follower = AddPursuingFollower(engine, GeoMath.ProjectPoint(baseStart, downwind.ToReciprocal(), 0.8), downwind);

        TickSeconds(engine, 2);
        output.WriteLine(string.Join(Environment.NewLine, follower.PendingWarnings));

        Assert.Equal(1, Calls(follower, STurnCall));
        Assert.Equal(0, Calls(follower, "extending downwind for spacing"));
    }

    /// <summary>
    /// The call's choice: an extended leg tracking within 30° of the circuit's downwind heading is an extended downwind; one 45°
    /// off it, or no extended leg, is an S-turn.
    /// </summary>
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(25.0, true)]
    [InlineData(-25.0, true)]
    [InlineData(45.0, false)]
    [InlineData(-45.0, false)]
    [InlineData(30.0, true)]
    [InlineData(31.0, false)]
    public void ExtendedLegWithinThirtyDegreesOfTheDownwind_IsExtendingDownwind(double offDownwindDeg, bool expected)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        TrueHeading legTrack = rwy.TrueHeading.ToReciprocal() + offDownwindDeg;
        var spacing = new FreePursuitSpacing(
            3.0,
            new FollowExcursionLimits(0.5, 45.0),
            new FollowCircuit(rwy, PatternDirection.Right),
            new FollowExtendedLeg(LeadBaseStart, legTrack, 1.0),
            rwy
        );

        Assert.Equal(expected, VfrFollowPhase.IsExtendingDownwind(spacing));
    }

    [Fact]
    public void NoExtendedLeg_IsNotExtendingDownwind()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        var spacing = new FreePursuitSpacing(3.0, new FollowExcursionLimits(0.5, 45.0), new FollowCircuit(rwy, PatternDirection.Right), null, rwy);

        Assert.False(VfrFollowPhase.IsExtendingDownwind(spacing));
    }
}

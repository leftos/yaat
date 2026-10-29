using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #1356 (YAAT-1327), the far-bar tail walk at a fillet: at SFO (S1-SFO-3) SKW305 lands 28R, exits on D and crosses 28L
/// from bar #852 to bar #853, where its exit route ends. The crossing must end with the tail past the far holding position
/// marking (AIM 2-3-5.a.1, AIM 4-3-21.b), driving straight on along D: 44 ft past the bar, D goes on straight (to #136) and two
/// fillets leave it onto B, each tangent to D at its start and turning 90 degrees. A walk that takes a fillet ends the
/// aircraft on B facing east, across the westbound taxi it is cleared next, and the replay's later traffic gridlocks on A.
/// </summary>
public sealed class Issue1356FarBarTailWalkStaysOnTaxiwayTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/sfo-gc-28rt-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "SKW305";
    private const int NearBarId = 852;
    private const int FarBarId = 853;

    /// <summary>The largest heading change (degrees) the tail walk may leave the aircraft with, the walk's own turn limit.</summary>
    private const double MaxTurnDeg = 45.0;

    private const string SfoCallsign = "UAL1356";

    [Fact]
    public void CrossingEndingOnFarBar_WalksStraightOnAlongD_NotOntoTheBFillet()
    {
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "SKW305's 28L bars on D are the recorded layout's nodes");
        if (groundData.GetLayout("SFO") is not { } layout)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(BundlePath);
        Assert.NotNull(archive);
        using (archive)
        {
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(archive.ToBaseSessionRecording(), 1511);
            AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));

            bool crossed = false;
            for (int t = 0; t < 90; t++)
            {
                engine.ReplayOneSecond();
                bool crossing = aircraft.Phases?.CurrentPhase is CrossingRunwayPhase { ApproachNodeId: NearBarId, TargetNodeId: FarBarId };
                crossed |= crossing;
                if (crossed && !crossing)
                {
                    break;
                }
            }

            Assert.True(crossed, $"test setup: {Callsign} crosses 28L from #{NearBarId} to #{FarBarId}");
            Assert.False(aircraft.Phases?.CurrentPhase is CrossingRunwayPhase, $"{Callsign} is still crossing 90 s on");

            GroundNode nearBar = layout.Nodes[NearBarId];
            GroundNode farBar = layout.Nodes[FarBarId];
            var across = new TrueHeading(GeoMath.BearingTo(nearBar.Position, farBar.Position));
            double turnDeg = GeoMath.AbsBearingDifference(across.Degrees, aircraft.TrueHeading.Degrees);
            double centrePastFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, farBar.Position, across) * GeoMath.FeetPerNm;
            double tailPastFt = centrePastFt - (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0);
            output.WriteLine(
                $"{Callsign} ended on {aircraft.Ground.CurrentTaxiway} heading {aircraft.TrueHeading.Degrees:F1} (across {across.Degrees:F1}, "
                    + $"turned {turnDeg:F1}), tail {tailPastFt:F1} ft past the #{FarBarId} hold line"
            );

            Assert.True(
                tailPastFt >= RunwayTailClearance.MarginFt - 1.0,
                $"{Callsign}'s tail is {tailPastFt:F1} ft past the 28L hold line at #{FarBarId}, "
                    + $"short of the {RunwayTailClearance.MarginFt:F0} ft margin"
            );
            Assert.True(
                turnDeg <= MaxTurnDeg,
                $"{Callsign} ended its 28L crossing on {aircraft.Ground.CurrentTaxiway} turned {turnDeg:F1} degrees from the way across "
                    + $"(limit {MaxTurnDeg:F0}): the tail walk took a fillet off D"
            );
        }
    }

    /// <summary>
    /// The far-bar tail walk toward another runway's bar it never reaches: at SFO a B738 on K at its B junction south of 28L
    /// is cleared to 28L, told to cross it, and its route ends on 28L's north bar on K. 191 ft on along K is 28R's south bar.
    /// The walk needs only half the length plus the margin past the 28L bar, so the nose stops well short of the 28R bar;
    /// refusing the K edge because it ends at the 28R bar leaves the tail inside 28L's holding position marking
    /// (AIM 2-3-5.a.1, AIM 4-3-21.b).
    /// </summary>
    [Fact]
    public void CrossingEndingOnFarBar_WalksOnTowardAnotherRunwaysBarItNeverReaches()
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        GroundNode start = GroundNodeLookup.Junction(ground.Layout, "K", "B");
        GroundNode otherBar = GroundNodeLookup.HoldShortBar(ground.Layout, "28R", "K", start);
        GroundNode farBar = GroundNodeLookup.HoldShortBar(ground.Layout, "28L", "K", otherBar);
        output.WriteLine($"K/B junction #{start.Id}, 28L north bar on K #{farBar.Id}, 28R south bar on K #{otherBar.Id}");
        var north = new TrueHeading(GeoMath.BearingTo(start.Position, farBar.Position));
        AircraftState aircraft = SfoGroundHarness.SpawnAt(ground, SfoCallsign, "B738", (start, north), new HoldingInPositionPhase());

        CommandResult taxi = ground.Engine.SendCommand(SfoCallsign, "TAXI K 28L");
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        int held = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase, 300, null);
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"holding at #{hold.HoldShort.NodeId} ({hold.HoldShort.Reason}) after {held} s");
        GroundNode nearBar = ground.Layout.Nodes[hold.HoldShort.NodeId];

        CommandResult cross = ground.Engine.SendCommand(SfoCallsign, "CROSS 28L");
        output.WriteLine($"cross: {cross.Success} — {cross.Message}");
        Assert.True(cross.Success, cross.Message);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.True(route.Segments[^1].ToNodeId == farBar.Id, $"test setup: the route ends at #{route.Segments[^1].ToNodeId}, not #{farBar.Id}");

        bool crossed = false;
        int done = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
            {
                bool crossing = aircraft.Phases?.CurrentPhase is CrossingRunwayPhase;
                crossed |= crossing;
                return crossed && !crossing && (aircraft.GroundSpeed < SfoGroundHarness.StationarySpeedKts);
            },
            180,
            null
        );
        Assert.True(done > 0, $"{SfoCallsign} never finished crossing 28L (phase {aircraft.Phases?.CurrentPhase?.Name})");

        var across = new TrueHeading(GeoMath.BearingTo(nearBar.Position, farBar.Position));
        double halfLengthFt = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0;
        double tailPastFt = (GeoMath.AlongTrackDistanceNm(aircraft.Position, farBar.Position, across) * GeoMath.FeetPerNm) - halfLengthFt;
        double nosePastOtherFt = (GeoMath.AlongTrackDistanceNm(aircraft.Position, otherBar.Position, across) * GeoMath.FeetPerNm) + halfLengthFt;
        output.WriteLine(
            $"{SfoCallsign} ended on {aircraft.Ground.CurrentTaxiway}: tail {tailPastFt:F1} ft past #{farBar.Id}, "
                + $"nose {nosePastOtherFt:F1} ft past #{otherBar.Id}"
        );

        Assert.True(
            tailPastFt >= RunwayTailClearance.MarginFt - 1.0,
            $"{SfoCallsign}'s tail is {tailPastFt:F1} ft past 28L's hold line at #{farBar.Id}, "
                + $"short of the {RunwayTailClearance.MarginFt:F0} ft margin"
        );
        Assert.True(nosePastOtherFt <= 0.0, $"{SfoCallsign}'s nose is {nosePastOtherFt:F1} ft past 28R's hold line at #{otherBar.Id}");
    }
}

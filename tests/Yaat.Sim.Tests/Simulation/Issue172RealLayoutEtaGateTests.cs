using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-bug #3/#6 (convergence-ETA gate) exercised against the REAL SFO ground layout,
/// independent of the issue172 recording. Two aircraft converge on the real M1×M3 crossing
/// (node 413 in the recording): a "winner" on M3 near the crossing and moving, and a "yielder" on
/// M1 farther away — the same geometry the recording reproduced. The winner clears the shared node
/// well before the yielder arrives, so the gate must not brake the yielder; when the winner is
/// instead stopped, the yielder must yield. Everything is derived from taxiway names and geometry,
/// so a coordinate-precision refresh of sfo.geojson (which renumbers nodes) does not break it.
/// </summary>
public class Issue172RealLayoutEtaGateTests
{
    private const double FtPerNm = 6076.12;

    public Issue172RealLayoutEtaGateTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Yielder_OnM1_NotBraked_WhenWinner_OnM3_ClearsCrossingFirst()
    {
        var setup = BuildM1M3Convergence(winnerIas: 18.0, yielderIas: 12.0);
        if (setup is null)
        {
            return;
        }

        GroundConflictDetector.ApplySpeedLimits([setup.Yielder, setup.Winner], setup.Layout);

        Assert.Null(setup.Yielder.Ground.SpeedLimit);
        Assert.Null(setup.Yielder.Ground.AutoYieldTarget);
    }

    [Fact]
    public void Yielder_OnM1_DoesYield_WhenWinner_OnM3_IsStopped()
    {
        var setup = BuildM1M3Convergence(winnerIas: 1.0, yielderIas: 12.0);
        if (setup is null)
        {
            return;
        }

        GroundConflictDetector.ApplySpeedLimits([setup.Yielder, setup.Winner], setup.Layout);

        // The nearer aircraft is essentially stopped (<= 3 kt), so it cannot be trusted to clear the
        // crossing first — the gate keeps the slowdown and the farther aircraft yields to it.
        Assert.Equal(setup.Winner.Callsign, setup.Yielder.Ground.AutoYieldTarget);
    }

    private sealed record Convergence(AirportGroundLayout Layout, AircraftState Winner, AircraftState Yielder);

    private static Convergence? BuildM1M3Convergence(double winnerIas, double yielderIas)
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        // node 413 in the recording — resolved by name so node renumbering does not matter.
        var crossing = layout.FindIntersectionNode("M1", "M3");
        Assert.NotNull(crossing);

        var winnerStart = NearestNodeOnTaxiwayInRange(layout, "M3", crossing.Position, 120.0, 450.0);
        var yielderStart = NearestNodeOnTaxiwayInRange(layout, "M1", crossing.Position, 700.0, 1400.0);
        Assert.NotNull(winnerStart);
        Assert.NotNull(yielderStart);

        var winnerRoute = TaxiPathfinder.FindRoute(layout, winnerStart.Id, crossing.Id, AircraftCategory.Jet);
        var yielderRoute = TaxiPathfinder.FindRoute(layout, yielderStart.Id, crossing.Id, AircraftCategory.Jet);
        Assert.NotNull(winnerRoute);
        Assert.NotNull(yielderRoute);

        // Both must approach the crossing from different directions for it to count as a convergence.
        Assert.Equal(crossing.Id, GroundConflictDetector.FindSharedUpcomingNode(yielderRoute, winnerRoute));

        var winner = MakeTaxiing("JBU", winnerStart, crossing.Position, winnerIas, winnerRoute);
        var yielder = MakeTaxiing("FFT", yielderStart, crossing.Position, yielderIas, yielderRoute);
        return new Convergence(layout, winner, yielder);
    }

    private static GroundNode? NearestNodeOnTaxiwayInRange(AirportGroundLayout layout, string taxiway, LatLon reference, double minFt, double maxFt)
    {
        double midFt = (minFt + maxFt) / 2.0;
        GroundNode? best = null;
        double bestErr = double.MaxValue;
        foreach (var node in layout.GetNodesOnTaxiway(taxiway))
        {
            double distFt = GeoMath.DistanceNm(node.Position, reference) * FtPerNm;
            if (distFt < minFt || distFt > maxFt)
            {
                continue;
            }

            double err = Math.Abs(distFt - midFt);
            if (err < bestErr)
            {
                bestErr = err;
                best = node;
            }
        }

        return best;
    }

    private static AircraftState MakeTaxiing(string callsign, GroundNode start, LatLon toward, double ias, TaxiRoute route)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = start.Position,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(start.Position, toward)),
            IsOnGround = true,
            IndicatedAirspeed = ias,
            Ground = new AircraftGroundOps { AssignedTaxiRoute = route },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new TaxiingPhase());
        ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return ac;
    }
}

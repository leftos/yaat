using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #234: an aircraft pulling up to SFO spot 7A stops with its CENTROID on the spot, so
/// half its fuselage juts forward toward taxiway A and <see cref="GroundConflictDetector"/> slows an
/// aircraft merely taxiing past on A. Two synthetic aircraft on the real SFO layout isolate the fix
/// (the bundle recording measures an unrelated RWY 28L-exit encroachment, and diverges under the
/// current routing code — it is not a reliable oracle here).
///
/// Geometry: spot 7A (node 6) sits ~175 ft off taxiway A down sub-lane T7A; the A/T7A junction is
/// node 48. A jet taxiing F10 -&gt; RWY 01L via <c>A M1</c> passes node 48. The conflict boundary is a
/// centroid separation of ~150-176 ft: a fuselage centered on the spot (nose ~130 ft from A) sits at
/// the edge, and any forward overshoot tips a passing jet into a full stop; nose-at-spot pulls the
/// centroid back a half-length (~220 ft from A), safely clear.
/// </summary>
public class Issue234Spot7AConflictTests(ITestOutputHelper output)
{
    private const string Taxiing = "TXA1"; // jet taxiing on A past the 7-ramp
    private const string SpotAc = "BLK1"; // aircraft occupying spot 7A

    /// <summary>
    /// The fix: a jet taxiing to spot 7A comes to rest with its nose (front of the footprint) at the
    /// spot marking, i.e. its centroid a half-fuselage SHORT of the spot node — not centered on it.
    /// Without the fix the centroid parks ~2 ft from the spot node (nose half a length past it toward A).
    /// </summary>
    [Fact]
    public void TaxiToSpot7A_StopsNoseAtSpot_NotCentroidOnSpot()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiingPhase", LogLevel.Debug).InitializeSimLog();

        var spot = layout.FindSpotNodeByName("7A");
        var f8 = layout.FindParkingByName("F8");
        if (spot is null || f8 is null)
        {
            return;
        }

        var engine = new SimulationEngine(groundData);
        engine.Scenario = MakeScenario();

        var jet = MakeGroundAircraft(Taxiing, "CRJ2", f8.Position, f8.TrueHeading ?? new TrueHeading(0), layout, new AtParkingPhase());
        engine.World.AddAircraft(jet);

        var cmd = engine.SendCommand(Taxiing, "TAXI T7A $7A");
        Assert.True(cmd.Success, $"TAXI command failed: {cmd.Message}");

        double lengthFt = FaaAircraftDatabase.Get("CRJ2")?.LengthFt ?? 88.0;
        double halfLenFt = lengthFt / 2.0;

        double restDistFt = double.NaN;
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Taxiing);
            if (ac is null)
            {
                break;
            }
            bool idle = ac.Phases?.CurrentPhase is HoldingInPositionPhase or AtParkingPhase;
            if (idle && ac.GroundSpeed < 0.5)
            {
                restDistFt = GeoMath.DistanceNm(ac.Position, spot.Position) * GeoMath.FeetPerNm;
                break;
            }
        }

        output.WriteLine($"CRJ2 taxied to $7A: rest centroid {restDistFt:F0} ft from spot node (half-length {halfLenFt:F0} ft).");

        Assert.False(double.IsNaN(restDistFt), "aircraft never settled at the spot");
        // Nose-at-spot: centroid rests roughly a half-length short of the spot node (allow braking slop),
        // not centered on it (~2 ft, the pre-fix behavior).
        Assert.True(
            restDistFt >= halfLenFt - 15.0,
            $"aircraft rested {restDistFt:F0} ft from the spot node — expected a nose-at-spot setback of ~{halfLenFt:F0} ft. "
                + "It parked centered on the spot (centroid on the marking), which juts the fuselage toward taxiway A (issue #234)."
        );
    }

    /// <summary>
    /// The mechanism the fix depends on: the spot aircraft's forward position gates whether a jet
    /// taxiing past on A is stopped. A B739 pulled back to nose-at-spot (or centered on the spot) leaves
    /// the passing jet at full taxi speed; the same aircraft overshot toward A stops the jet dead. This
    /// guards that the conflict boundary sits between "centered on the spot" and "overshoot toward A",
    /// i.e. that pulling the spot aircraft back keeps taxiway A moving.
    /// </summary>
    [Fact]
    public void SpotAircraftForwardPosition_GatesTaxiwayASpeed()
    {
        var noseAtSpot = RunPass(forwardOffsetFt: -69); // pulled back a half B739 length
        var centroid = RunPass(forwardOffsetFt: 0); // centered on the spot marking
        var overshoot = RunPass(forwardOffsetFt: 75); // stopped too far forward, toward A

        if (noseAtSpot is null || centroid is null || overshoot is null)
        {
            return;
        }

        void Dump(string label, PassResult r) =>
            output.WriteLine(
                $"{label, -16} reached={r.Reached} minGs={r.MinGs:F1}kt nearStopTicks={r.NearStopTicks} minDistToJet={r.MinDistFt:F0}ft"
            );

        Dump("nose-at-spot", noseAtSpot.Value);
        Dump("centroid@spot", centroid.Value);
        Dump("overshoot->A", overshoot.Value);

        Assert.True(noseAtSpot.Value.Reached && centroid.Value.Reached && overshoot.Value.Reached, "jet did not reach the ramp in one of the runs");

        // Overshoot toward A stops the passing jet dead; pulling the aircraft back to nose-at-spot leaves
        // it taxiing freely. (Centered on the spot is already clear here, but sits at the boundary.)
        Assert.True(
            overshoot.Value.NearStopTicks > 20,
            $"overshoot did not stop the passing jet (nearStopTicks={overshoot.Value.NearStopTicks}); the reproduction geometry has drifted."
        );
        Assert.True(
            noseAtSpot.Value.MinGs > overshoot.Value.MinGs + 10.0,
            $"nose-at-spot did not keep taxiway A moving: nose minGs={noseAtSpot.Value.MinGs:F1}kt vs overshoot minGs={overshoot.Value.MinGs:F1}kt."
        );
        Assert.Equal(0, noseAtSpot.Value.NearStopTicks);
    }

    private readonly record struct PassResult(bool Reached, double MinGs, double MinDistFt, int NearStopTicks);

    private PassResult? RunPass(double forwardOffsetFt)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var spot = layout.FindSpotNodeByName("7A");
        var aJunction = layout.FindIntersectionNode("A", "T7A");
        var f10 = layout.FindParkingByName("F10");
        if (spot is null || aJunction is null || f10 is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        engine.Scenario = MakeScenario();

        // Spot occupant (B739) facing taxiway A, centroid offset forwardOffsetFt toward A from the marking.
        double hdgToA = GeoMath.BearingTo(spot.Position, aJunction.Position);
        var occPos = GeoMath.ProjectPoint(spot.Position, new TrueHeading(hdgToA), forwardOffsetFt / GeoMath.FeetPerNm);
        var occ = MakeGroundAircraft(SpotAc, "B739", occPos, new TrueHeading(hdgToA), layout, new HoldingInPositionPhase());
        occ.Ground.CurrentTaxiway = "T7A";
        engine.World.AddAircraft(occ);

        var jet = MakeGroundAircraft(Taxiing, "E75L", f10.Position, f10.TrueHeading ?? new TrueHeading(0), layout, new AtParkingPhase());
        engine.World.AddAircraft(jet);
        var cmd = engine.SendCommand(Taxiing, "TAXI A M1 01L");
        if (!cmd.Success)
        {
            return null;
        }

        double minGs = double.MaxValue;
        double minDist = double.MaxValue;
        int nearStop = 0;
        bool reached = false;
        bool passed = false;

        for (int t = 1; t <= 220 && !passed; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Taxiing);
            if (ac is null)
            {
                break;
            }

            double dRampFt = GeoMath.DistanceNm(ac.Position, aJunction.Position) * GeoMath.FeetPerNm;
            bool onA = string.Equals(ac.Ground.CurrentTaxiway, "A", StringComparison.OrdinalIgnoreCase);
            if (dRampFt <= 260 && onA)
            {
                reached = true;
                minGs = Math.Min(minGs, ac.GroundSpeed);
                var occAc = engine.FindAircraft(SpotAc);
                if (occAc is not null)
                {
                    minDist = Math.Min(minDist, GeoMath.DistanceNm(ac.Position, occAc.Position) * GeoMath.FeetPerNm);
                }
                if (ac.GroundSpeed < 3.0)
                {
                    nearStop++;
                }
            }
            else if (reached && dRampFt > 260)
            {
                passed = true;
            }
        }

        return new PassResult(reached, reached ? minGs : 0, reached ? minDist : -1, nearStop);
    }

    private static SimScenarioState MakeScenario() =>
        new()
        {
            ScenarioId = "issue234-synth",
            ScenarioName = "issue234-synth",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
            AutoCrossRunway = false,
        };

    private static AircraftState MakeGroundAircraft(
        string callsign,
        string type,
        LatLon pos,
        TrueHeading hdg,
        AirportGroundLayout layout,
        Phase startPhase
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pos,
            TrueHeading = hdg,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(startPhase);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, layout));
        ac.Ground.Layout = layout;
        return ac;
    }
}

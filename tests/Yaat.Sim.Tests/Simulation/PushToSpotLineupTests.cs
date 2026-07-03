using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// PUSH $spot must position the aircraft like a tug does: reverse in an arc PAST the spot to a staging
/// point behind it, then pull FORWARD onto the marking, coming to rest lined up straight along the
/// sub-lane with the NOSEWHEEL on the spot (centroid a half-fuselage back) and the nose facing OUT
/// toward the parent taxiway. Before the fix, <see cref="PushbackPhase"/> reversed the aircraft's
/// CENTROID directly onto the spot node at whatever arbitrary heading the reverse arc left it — the
/// pushback twin of the taxi-to-spot centroid-on-spot defect fixed in issue #234.
///
/// Geometry (real SFO layout): spot 7A (node 6) sits on ramp sub-lane T7A. Its outbound edge points at
/// the A/T7A junction (bearing ~27°, the movement area); the reciprocal (~207°) leads deeper into the
/// RAMP. Nose-out therefore faces ~27°, and the staging point sits along ~207° behind the marking.
/// </summary>
public class PushToSpotLineupTests(ITestOutputHelper output)
{
    private const string Pushed = "PSH1";

    [Fact]
    public void PushToSpot7A_EndsNoseOutNosewheelOnSpot_ViaReversePastThenForward()
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

        SimLogBuilder.CreateForTest(output).EnableCategory("PushbackPhase", LogLevel.Debug).InitializeSimLog();

        var spot = layout.FindSpotNodeByName("7A");
        var aJunction = layout.FindIntersectionNode("A", "T7A");
        if (spot is null || aJunction is null)
        {
            return;
        }

        // Outbound (nose-out) heading: from the spot toward the A/T7A junction (the movement area).
        double hdgOut = GeoMath.BearingTo(spot.Position, aJunction.Position);
        double hdgIntoRamp = new TrueHeading(hdgOut).ToReciprocal().Degrees;

        double lengthFt = FaaAircraftDatabase.Get("CRJ2")?.LengthFt ?? 88.0;
        double halfLenFt = lengthFt / 2.0;

        // Start the aircraft off to one side of the spot (25° off the sub-lane axis, toward taxiway A),
        // nose facing out — as if it just came off a gate and must be pushed back into the ramp spot.
        // Off-axis is deliberate: pre-fix the reverse ends the aircraft facing start->spot's reciprocal
        // (not the sub-lane's out-heading), so the facing assertion is a real red/green discriminator.
        double startBearing = new TrueHeading(hdgOut - 45.0).Degrees;
        var startPos = GeoMath.ProjectPoint(spot.Position, new TrueHeading(startBearing), 150.0 / GeoMath.FeetPerNm);

        var engine = new SimulationEngine(groundData);
        engine.Scenario = MakeScenario();

        var ac = MakeGroundAircraft(Pushed, "CRJ2", startPos, new TrueHeading(hdgOut), layout, new AtParkingPhase());
        engine.World.AddAircraft(ac);

        var cmd = engine.SendCommand(Pushed, "PUSH $7A");
        Assert.True(cmd.Success, $"PUSH command failed: {cmd.Message}");

        // Signed depth is negative out toward the taxiway (start side) and positive behind the spot in
        // the ramp, so the max over the whole run is the staging point — no need to gate on passing.
        double maxDepthIntoRampFt = double.MinValue; // deepest the centroid gets behind the spot
        double finalDistFt = double.NaN;
        double finalHdg = double.NaN;
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            var a = engine.FindAircraft(Pushed);
            if (a is null)
            {
                break;
            }

            double distFt = GeoMath.DistanceNm(a.Position, spot.Position) * GeoMath.FeetPerNm;
            maxDepthIntoRampFt = Math.Max(maxDepthIntoRampFt, DepthIntoRamp(a.Position, spot.Position, hdgIntoRamp));

            bool done = a.Phases?.CurrentPhase is AtParkingPhase && a.GroundSpeed < 0.5;
            if (done && t > 3)
            {
                finalDistFt = distFt;
                finalHdg = a.TrueHeading.Degrees;
                break;
            }
        }

        output.WriteLine(
            $"PUSH $7A: rest {finalDistFt:F0}ft from spot (half-length {halfLenFt:F0}ft), nose {finalHdg:F0}° "
                + $"(out={hdgOut:F0}°), deepest-behind {maxDepthIntoRampFt:F0}ft."
        );

        Assert.False(double.IsNaN(finalDistFt), "aircraft never settled at the spot");

        // 1. Nosewheel on the spot: centroid rests ~half a fuselage behind the marking (not centered on
        //    it, the pre-fix ~0-3 ft).
        Assert.True(
            finalDistFt >= halfLenFt - 15.0 && finalDistFt <= halfLenFt + 30.0,
            $"rested {finalDistFt:F0}ft from the spot — expected a nose-at-spot setback of ~{halfLenFt:F0}ft. "
                + "Centroid-on-spot juts the fuselage a half-length past the marking toward taxiway A."
        );

        // 2. Lined up straight, nose facing OUT toward the taxiway (pre-fix: arbitrary heading).
        double hdgErr = new TrueHeading(hdgOut).AbsAngleTo(new TrueHeading(finalHdg));
        Assert.True(hdgErr <= 12.0, $"nose ended {finalHdg:F0}° — expected ~{hdgOut:F0}° (out toward the taxiway), off by {hdgErr:F0}°.");

        // 3. Reversed PAST the spot, then pulled forward: the centroid went deeper into the ramp than its
        //    final rest before coming forward onto the marking (pre-fix: single reverse straight to the
        //    spot, never overshooting behind it).
        Assert.True(
            maxDepthIntoRampFt > finalDistFt + 20.0,
            $"no reverse-past-then-forward: deepest-behind was {maxDepthIntoRampFt:F0}ft vs final {finalDistFt:F0}ft. "
                + "Expected the tug to overshoot behind the spot then pull forward to line up."
        );
    }

    /// <summary>
    /// A snapshot taken mid-pull-forward must round-trip: the DTO carries the forward-leg flag and rest
    /// target, and <see cref="PushbackPhase.FromSnapshot"/> restores them so the restored phase resumes the
    /// forward leg (guards the new DTO fields against a future serialization regression).
    /// </summary>
    [Fact]
    public void PushToSpot7A_SnapshotMidPullForward_RoundTripsForwardLeg()
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

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var spot = layout.FindSpotNodeByName("7A");
        var aJunction = layout.FindIntersectionNode("A", "T7A");
        if (spot is null || aJunction is null)
        {
            return;
        }

        double hdgOut = GeoMath.BearingTo(spot.Position, aJunction.Position);
        var startPos = GeoMath.ProjectPoint(spot.Position, new TrueHeading(hdgOut), 140.0 / GeoMath.FeetPerNm);

        var engine = new SimulationEngine(groundData);
        engine.Scenario = MakeScenario();
        var ac = MakeGroundAircraft(Pushed, "CRJ2", startPos, new TrueHeading(hdgOut), layout, new AtParkingPhase());
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand(Pushed, "PUSH $7A").Success);

        // Tick until the pushback is on its forward (pull-onto-spot) leg.
        PushbackPhaseDto? midDto = null;
        for (int t = 1; t <= 240 && midDto is null; t++)
        {
            engine.TickOneSecond();
            if (engine.FindAircraft(Pushed)?.Phases?.CurrentPhase is PushbackPhase p && p.ToSnapshot() is PushbackPhaseDto { PullingForward: true } d)
            {
                midDto = d;
            }
        }

        Assert.NotNull(midDto);
        Assert.NotNull(midDto!.PullForwardLatitude);
        Assert.NotNull(midDto.PullForwardLongitude);

        // FromSnapshot must resume the forward leg with the same rest target, staging target, and heading.
        var reDto = (PushbackPhaseDto)PushbackPhase.FromSnapshot(midDto).ToSnapshot();
        Assert.True(reDto.PullingForward, "restored phase should still be on the forward leg");
        Assert.Equal(midDto.PullForwardLatitude, reDto.PullForwardLatitude);
        Assert.Equal(midDto.PullForwardLongitude, reDto.PullForwardLongitude);
        Assert.Equal(midDto.TargetLatitude, reDto.TargetLatitude);
        Assert.Equal(midDto.TargetLongitude, reDto.TargetLongitude);
        Assert.Equal(midDto.TargetHeading, reDto.TargetHeading);
        Assert.Equal(midDto.ReachedTarget, reDto.ReachedTarget);
    }

    /// <summary>
    /// Signed distance of a position "into the ramp" from the spot along the inbound (ramp-side) axis.
    /// Positive = behind the spot (ramp side); negative = out toward the taxiway.
    /// </summary>
    private static double DepthIntoRamp(LatLon pos, LatLon spot, double hdgIntoRamp)
    {
        double distFt = GeoMath.DistanceNm(pos, spot) * GeoMath.FeetPerNm;
        double bearing = GeoMath.BearingTo(spot, pos);
        double err = new TrueHeading(hdgIntoRamp).AbsAngleTo(new TrueHeading(bearing));
        return err <= 90.0 ? distFt : -distFt;
    }

    private static SimScenarioState MakeScenario() =>
        new()
        {
            ScenarioId = "push-spot-synth",
            ScenarioName = "push-spot-synth",
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
                CruiseAltitude = 30000,
            },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(startPhase);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, layout));
        ac.Ground.Layout = layout;
        return ac;
    }
}

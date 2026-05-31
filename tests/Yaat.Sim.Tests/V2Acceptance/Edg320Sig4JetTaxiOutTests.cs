using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.V2Acceptance;

/// <summary>
/// V2-stack regression for the EDG320 ramp-corner wiggle (from the OAK north-field
/// spin bundle, where SIG4 hosts a jet). A B738 taxiing out of SIG4 (node 641,
/// parked heading ~110°) to 28R must make a ~109° turn leaving the spot and a ~86°
/// turn onto taxiway D. Under the full V2 stack the navigator rounds each corner at
/// the nose-wheel radius but — before the tangent-corner-rounding fix — finished each
/// arc displaced off the outgoing centerline, then re-acquired it with pure-pursuit
/// while accelerating, overshooting ~40° per corner and wobbling ~400° cumulative.
///
/// The guard mirrors <see cref="Simulation.OakNorthFieldTaxiSpinTests"/>: a clean
/// taxi-out of a north-field spot needs &lt; 200° of <em>signed</em> rotation and
/// well under 320° <em>absolute</em> over the first 30 s. The overshoot wobble blows
/// past the absolute bound.
/// </summary>
[Collection("V2 Acceptance")]
public class Edg320Sig4JetTaxiOutTests(ITestOutputHelper output)
{
    private const double MaxCumulativeAbsDeg = 320.0;
    private const double MaxAbsSignedDeg = 200.0;
    private const double MinProgressFt = 500.0;

    [Fact]
    public void JetTaxiOutOfSig4_StaysWithinTurnEnvelope_OnV2()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: NavigationDb not initialized");
            return;
        }

        var groundData = new TestAirportGroundData(FilletMode.Standard);
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        var layout = groundData.GetLayout("OAK");
        Assert.NotNull(layout);

        var origin = layout.FindParkingByName("SIG4");
        Assert.NotNull(origin);

        var engine = new SimulationEngine(groundData);
        var aircraft = new AircraftState
        {
            Callsign = "EDG320",
            AircraftType = "B738",
            Position = origin.Position,
            TrueHeading = origin.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                CruiseAltitude = 1500,
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "edg320-sig4",
            ScenarioName = "edg320-sig4",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunway = true,
        };

        var result = engine.SendCommand("EDG320", "TAXIAUTO 28R");
        Assert.True(result.Success, $"command failed: {result.Message}");

        var startPos = aircraft.Position;
        double prevHdg = aircraft.TrueHeading.Degrees;
        double cumAbs30 = 0;
        double cumSigned30 = 0;
        double movedFt60 = 0;

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            double hdg = aircraft.TrueHeading.Degrees;
            double dHdg = (((hdg - prevHdg) + 540.0) % 360.0) - 180.0;
            prevHdg = hdg;
            if (t <= 30)
            {
                cumAbs30 += Math.Abs(dHdg);
                cumSigned30 += dHdg;
            }
            movedFt60 = GeoMath.DistanceNm(startPos, aircraft.Position) * GeoMath.FeetPerNm;
            output.WriteLine(
                $"t={t, -3} hdg={hdg, 5:F0} dHdg={dHdg, 5:F0} cumAbs={cumAbs30, 5:F0} gs={aircraft.GroundSpeed, 5:F1} "
                    + $"seg={aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex} phase={aircraft.Phases?.CurrentPhase?.Name}"
            );
        }

        output.WriteLine($"EDG320 SIG4→28R (V2): cumAbs30={cumAbs30:F0}° cumSigned30={cumSigned30:F0}° moved60={movedFt60:F0}ft");

        Assert.True(
            movedFt60 >= MinProgressFt,
            $"EDG320 only moved {movedFt60:F0}ft in 60s — expected ≥ {MinProgressFt:F0}ft (orbiting the ramp corner)."
        );
        Assert.True(
            Math.Abs(cumSigned30) <= MaxAbsSignedDeg,
            $"EDG320 accumulated {cumSigned30:F0}° signed rotation in 30s — expected |signed| ≤ {MaxAbsSignedDeg:F0}°."
        );
        Assert.True(
            cumAbs30 <= MaxCumulativeAbsDeg,
            $"EDG320 accumulated {cumAbs30:F0}° absolute rotation in 30s — expected ≤ {MaxCumulativeAbsDeg:F0}°. "
                + "Corner-rounding that lands off the outgoing centerline forces a pure-pursuit re-acquisition that overshoots."
        );
    }
}

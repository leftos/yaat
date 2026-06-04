using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// E2E: LINDZ ONE departure out of KASE (Aspen) RWY 33 — VA "climb heading 343° to 9100",
/// VI "climbing left turn to 273° to intercept", CF "303° back course to LINDZ". Drives the
/// real sim (phases + FlightPhysics) and verifies the aircraft climbs through 9100 before turning,
/// flies the 273° leg, then intercepts/tracks the 303° course toward LINDZ — instead of turning
/// direct to LINDZ at 400 ft AGL.
/// </summary>
[Collection("NavDbMutator")]
public class KaseLindz1DepartureE2ETests
{
    private readonly ITestOutputHelper _output;

    public KaseLindz1DepartureE2ETests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo? Kase33()
    {
        var phys = NavigationDatabase
            .Instance.GetRunways("KASE")
            .FirstOrDefault(r => r.Id.End1 == "33" || r.Id.End2 == "33" || r.Designator.Contains("33", StringComparison.Ordinal));
        if (phys is null)
        {
            return null;
        }
        return new RunwayInfo
        {
            AirportId = "KASE",
            Id = phys.Id,
            Designator = "33",
            Lat1 = phys.Lat1,
            Lon1 = phys.Lon1,
            Elevation1Ft = phys.Elevation1Ft,
            TrueHeading1 = phys.TrueHeading1,
            Lat2 = phys.Lat2,
            Lon2 = phys.Lon2,
            Elevation2Ft = phys.Elevation2Ft,
            TrueHeading2 = phys.TrueHeading2,
            LengthFt = phys.LengthFt,
            WidthFt = phys.WidthFt,
        };
    }

    [Fact]
    public void FliesChartedClimbThenLeftTurnThenBackCourse()
    {
        var rwy = Kase33();
        if (rwy is null)
        {
            _output.WriteLine("KASE RWY33 not in test nav data — skipping.");
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KASE",
                Destination = "KSFO",
                Route = "LINDZ1 LINDZ JNC BEVRR KATTS INYOE DYAMD5",
                CruiseAltitude = 33000,
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var holding = new HoldingInPositionPhase();
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: null,
            NullLogger.Instance
        );
        _output.WriteLine($"CTO success={result.Success}: {result.Message}");

        var climb = ac.Phases.Phases.OfType<InitialClimbPhase>().FirstOrDefault();
        _output.WriteLine($"InitialClimb procedureLegs={climb?.DepartureProcedureLegs?.Count.ToString() ?? "null"}");
        if (climb?.DepartureProcedureLegs is { } legs)
        {
            foreach (var l in legs)
            {
                _output.WriteLine($"  leg {l.Type} course={l.CourseMagnetic} alt={l.TargetAltitudeFt} fix={l.FixName}");
            }
        }

        // Airborne just past the DER, above the 400 ft AGL turn floor.
        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.3);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + 450;
        ac.IndicatedAirspeed = 210;

        var cat = AircraftCategorization.Categorize(ac.AircraftType);
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = cat,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
        ac.Phases.SkipTo<InitialClimbPhase>(ctx);

        Assert.NotNull(climb!.DepartureProcedureLegs);
        Assert.Equal(ProcedureLegType.HeadingToAltitude, climb.DepartureProcedureLegs![0].Type);
        Assert.Equal(ProcedureLegType.HeadingToIntercept, climb.DepartureProcedureLegs[1].Type);
        Assert.Equal(ProcedureLegType.CourseToFix, climb.DepartureProcedureLegs[2].Type);

        var lindzPos = NavigationDatabase.Instance.GetFixPosition("LINDZ");
        Assert.NotNull(lindzPos);
        var lindz = new LatLon(lindzPos!.Value.Lat, lindzPos.Value.Lon);
        double rwyTrue = rwy.TrueHeading.Degrees;

        var samples = new List<(int T, double Alt, double Hdg, double Tgt, double Dist)>();
        for (int t = 0; t < 360; t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0, null, null);

            string phase = ac.Phases.CurrentPhase?.Name ?? "(none)";
            double tgt = ac.Targets.TargetTrueHeading?.Degrees ?? -1;
            double dist = GeoMath.DistanceNm(ac.Position, lindz);
            samples.Add((t, ac.Altitude, ac.TrueHeading.Degrees, tgt, dist));
            if (t % 10 == 0)
            {
                _output.WriteLine(
                    $"t={t, 3} {phase, -18} alt={ac.Altitude, 6:F0} hdg={ac.TrueHeading.Degrees, 3:F0} tgt={tgt, 3:F0} route={ac.Targets.NavigationRoute.Count} distLINDZ={dist:F1}"
                );
            }
            if (ac.Targets.NavigationRoute.Count > 0 && phase is not ("DepartureProcedure" or "InitialClimb"))
            {
                break;
            }
        }

        // Magnetic declination resolved once the aircraft is positioned (≈ +8° E at Aspen).
        double decl = ac.Declination;
        double viTrue = new MagneticHeading(273).ToTrue(decl).Degrees;
        double cfTrue = new MagneticHeading(303).ToTrue(decl).Degrees;

        // (1) Climbs to ≥9100 before the climbing LEFT turn (old code turned direct to LINDZ
        // at ~400 ft AGL ≈ 8240 ft). The VA leg flies ~runway heading and slightly right (343°),
        // so heading dipping clearly below runway heading marks the start of the 273° left turn.
        var leftTurn = samples.FirstOrDefault(s => s.T > 1 && s.Hdg < rwyTrue - 10);
        Assert.True(leftTurn.Dist > 0, "aircraft never started the left turn");
        Assert.True(
            leftTurn.Alt >= 9100,
            $"left turn began at {leftTurn.Alt:F0} ft — should be ≥9100 (VA gate), not a direct-to-LINDZ turn at ~400 AGL"
        );

        // (2) Flew the published 273° leg (≈281° true) while above 9100.
        Assert.Contains(samples, s => Math.Abs(s.Tgt - viTrue) < 6 && s.Alt >= 9100);

        // (3) Intercepted and tracked the 303° back course (≈311° true) inbound to LINDZ.
        Assert.Contains(samples, s => Math.Abs(s.Tgt - cfTrue) < 6);
        Assert.True(samples[^1].Dist < 0.6, $"never reached LINDZ — closest {samples.Min(s => s.Dist):F2} nm");

        // (4) Handed off to the enroute transition (JNC …) after overflying LINDZ.
        Assert.NotEqual("DepartureProcedure", ac.Phases.CurrentPhase?.Name);
        Assert.Contains(ac.Targets.NavigationRoute, n => n.Name == "JNC");
    }
}

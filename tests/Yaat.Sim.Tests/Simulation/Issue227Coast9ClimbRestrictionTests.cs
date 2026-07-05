using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #227: aircraft ignore the COAST9 climb restriction out of KOAK.
/// The COAST9 RWY 30 transition opens with an ARINC-424 VD leg (heading 296° to OAK 4.0 DME,
/// altitude "between 1400 and 2000"). The aircraft must level off at/below 2000 ft until it
/// passes OAK 4.0 DME, then resume the climb — instead of climbing straight through the window.
/// Drives the real sim (phases + FlightPhysics) with real COAST9 CIFP data.
/// </summary>
[Collection("NavDbMutator")]
public class Issue227Coast9ClimbRestrictionTests
{
    private const double WindowCeilingFt = 2000.0;
    private const double DmeFixNm = 4.0;

    private readonly ITestOutputHelper _output;

    public Issue227Coast9ClimbRestrictionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo? Koak30()
    {
        var phys = NavigationDatabase
            .Instance.GetRunways("KOAK")
            .FirstOrDefault(r => r.Id.End1 == "30" || r.Id.End2 == "30" || r.Designator.Contains("30", StringComparison.Ordinal));
        if (phys is null)
        {
            return null;
        }
        return new RunwayInfo
        {
            AirportId = "KOAK",
            Id = phys.Id,
            Designator = "30",
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
    public void Coast9_LevelsOffAtWindowCeiling_UntilOak4Dme_ThenResumesClimb()
    {
        var rwy = Koak30();
        if (rwy is null)
        {
            _output.WriteLine("KOAK RWY30 not in test nav data — skipping.");
            return;
        }

        // Ensure the COAST9 procedure and the OAK reference navaid are available in test data.
        var oakPos = NavigationDatabase.Instance.GetFixPosition("OAK");
        if (oakPos is null || NavigationDatabase.Instance.GetSid("KOAK", "COAST9") is null)
        {
            _output.WriteLine("COAST9 / OAK not in test nav data — skipping.");
            return;
        }
        var oak = new LatLon(oakPos.Value.Lat, oakPos.Value.Lon);

        var ac = new AircraftState
        {
            Callsign = "SWA1822",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSAN",
                Route = "COAST9 MCKEY LAX COMIX2",
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy };

        var holding = new HoldingInPositionPhase();
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var cto = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: null,
            NullLogger.Instance
        );
        Assert.True(cto.Success, cto.Message);

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
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.2);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + 450;
        ac.IndicatedAirspeed = 170;

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

        double maxAltInsideDme = 0;
        double altAtResume = 0;
        double resumeDme = 0;
        bool violated = false;
        double worstAltInsideDme = 0;
        double worstDmeAtViolation = 0;

        for (int t = 0; t < 360; t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0, null, null);

            double dme = GeoMath.DistanceNm(ac.Position, oak);
            string phase = ac.Phases.CurrentPhase?.Name ?? "(none)";

            if (t % 10 == 0)
            {
                _output.WriteLine(
                    $"t={t, 3} {phase, -18} alt={ac.Altitude, 6:F0} vs={ac.VerticalSpeed, 6:F0} dmeOAK={dme:F2} tgtAlt={ac.Targets.TargetAltitude?.ToString() ?? "-"}"
                );
            }

            if (dme < DmeFixNm)
            {
                maxAltInsideDme = Math.Max(maxAltInsideDme, ac.Altitude);
                if (ac.Altitude > WindowCeilingFt + 250 && !violated)
                {
                    violated = true;
                    worstAltInsideDme = ac.Altitude;
                    worstDmeAtViolation = dme;
                }
            }
            else if (altAtResume == 0)
            {
                altAtResume = ac.Altitude;
                resumeDme = dme;
            }

            // Stop once well past the DME fix and climbing again.
            if (dme > DmeFixNm + 1.0 && ac.Altitude > WindowCeilingFt + 500)
            {
                break;
            }
        }

        _output.WriteLine($"maxAltInsideDme={maxAltInsideDme:F0} altAtResume={altAtResume:F0} resumeDme={resumeDme:F2}");

        // (1) The aircraft must NOT exceed the 2000 ft window ceiling while inside OAK 4.0 DME.
        Assert.False(
            violated,
            $"Aircraft climbed to {worstAltInsideDme:F0} ft at {worstDmeAtViolation:F2} DME — above the COAST9 2000 ft ceiling inside OAK 4.0 DME"
        );

        // (2) It actually climbed up to the ceiling (leveled off), not merely stayed low.
        Assert.True(
            maxAltInsideDme >= 1800,
            $"Aircraft only reached {maxAltInsideDme:F0} ft inside 4 DME — expected to climb to ~2000 and level off"
        );

        // (3) It resumed climbing above the window once past OAK 4.0 DME.
        Assert.True(altAtResume >= 1800, $"Aircraft was at {altAtResume:F0} ft when it passed OAK 4.0 DME");
        Assert.True(ac.Altitude > WindowCeilingFt + 400, $"Aircraft did not resume the climb after OAK 4.0 DME (ended at {ac.Altitude:F0} ft)");
    }
}

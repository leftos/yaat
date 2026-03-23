using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for SID climb-via planning on the CNDEL5 departure from KOAK
/// with SUSEY enroute transition. Verifies the aircraft meets each altitude
/// constraint at each fix using step-climb planning with variable climb rate.
/// </summary>
public class Cndel5ClimbProfileTests(ITestOutputHelper output)
{
    /// <summary>
    /// Build the CNDEL5 SUSEY route from CIFP data and verify the aircraft
    /// meets all altitude constraints during climb-via.
    /// </summary>
    [Fact]
    public void ClimbVia_Cndel5Susey_MeetsAllConstraints()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            output.WriteLine("Skipped: NavData not available");
            return;
        }

        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        // Resolve CNDEL5 SID from CIFP
        var sid = navDb.GetSid("KOAK", "CNDEL5");
        if (sid is null)
        {
            output.WriteLine("Skipped: CNDEL5 SID not found in CIFP data");
            return;
        }

        // Build ordered legs: runway transition (RW30) → common → enroute transition (SUSEY)
        var orderedLegs = new List<CifpLeg>();

        // Runway transition for RW30
        if (sid.RunwayTransitions.TryGetValue("RW30", out var rwTransition))
        {
            orderedLegs.AddRange(rwTransition.Legs);
        }
        else
        {
            // Try "both" key
            foreach (var key in sid.RunwayTransitions.Keys)
            {
                output.WriteLine($"  Available runway transition: {key}");
            }

            output.WriteLine("Skipped: No RW30 transition found for CNDEL5");
            return;
        }

        orderedLegs.AddRange(sid.CommonLegs);

        if (sid.EnrouteTransitions.TryGetValue("SUSEY", out var enTransition))
        {
            orderedLegs.AddRange(enTransition.Legs);
        }
        else
        {
            foreach (var key in sid.EnrouteTransitions.Keys)
            {
                output.WriteLine($"  Available enroute transition: {key}");
            }

            output.WriteLine("Skipped: No SUSEY transition found for CNDEL5");
            return;
        }

        // Convert to navigation targets
        var route = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);
        Assert.NotEmpty(route);

        output.WriteLine($"CNDEL5 RW30 → SUSEY route ({route.Count} fixes):");
        foreach (var fix in route)
        {
            string alt = fix.AltitudeRestriction is not null ? $" [{fix.AltitudeRestriction}]" : "";
            string spd = fix.SpeedRestriction is not null ? $" [{fix.SpeedRestriction}]" : "";
            output.WriteLine($"  {fix.Name} ({fix.Latitude:F4}, {fix.Longitude:F4}){alt}{spd}");
        }

        // Create aircraft at OAK runway 30 departure end, airborne at 1000ft, 180kts
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = route[0].Latitude,
            Longitude = route[0].Longitude,
            TrueHeading = new TrueHeading(300), // RW30 heading
            TrueTrack = new TrueHeading(300),
            Altitude = 1000,
            IndicatedAirspeed = 180,
            Departure = "KOAK",
            ActiveSidId = "CNDEL5",
            SidViaMode = true,
        };

        foreach (var target in route)
        {
            aircraft.Targets.NavigationRoute.Add(target);
        }

        // Apply first constrained fix
        FlightPhysics.ApplyFixConstraints(aircraft, route.First(t => t.AltitudeRestriction is not null || t.SpeedRestriction is not null));

        output.WriteLine(
            $"\nAircraft at start: alt={aircraft.Altitude:F0} tgtAlt={aircraft.Targets.TargetAltitude} SidViaMode={aircraft.SidViaMode}"
        );

        // Collect constraint fixes and their expected altitudes
        var constraintFixes = route.Where(t => t.AltitudeRestriction is not null).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var crossingAltitudes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Tick up to 30 minutes (1800s)
        output.WriteLine($"\n{"t", 5} {"Alt", 7} {"TgtAlt", 7} {"VS", 6} {"NextFix", -10}");
        for (int t = 1; t <= 1800; t++)
        {
            FlightPhysics.Update(aircraft, 1.0);

            // Check for sequenced constraint fixes
            foreach (var fixName in constraintFixes.ToList())
            {
                if (
                    !aircraft.Targets.NavigationRoute.Any(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase))
                    && !crossingAltitudes.ContainsKey(fixName)
                )
                {
                    crossingAltitudes[fixName] = aircraft.Altitude;
                    output.WriteLine($"  >>> {fixName} sequenced at t={t}: alt={aircraft.Altitude:F0}");
                }
            }

            if (t % 30 == 0)
            {
                var nextFix = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"{t, 5} {aircraft.Altitude, 7:F0} {aircraft.Targets.TargetAltitude?.ToString("F0") ?? "null", 7} {aircraft.VerticalSpeed, 6:F0} {nextFix, -10}"
                );
            }

            // Stop once all constraint fixes are sequenced or route is empty
            if (aircraft.Targets.NavigationRoute.Count == 0)
            {
                break;
            }
        }

        // Verify each constraint was met
        output.WriteLine("\nConstraint crossing summary:");
        foreach (var fix in route.Where(t => t.AltitudeRestriction is not null))
        {
            if (!crossingAltitudes.TryGetValue(fix.Name, out double crossAlt))
            {
                output.WriteLine($"  {fix.Name}: NOT SEQUENCED (aircraft may not have reached it)");
                continue;
            }

            var restriction = fix.AltitudeRestriction!;
            bool met = IsConstraintMet(crossAlt, restriction);
            string status = met ? "OK" : "MISSED";
            output.WriteLine($"  {fix.Name}: crossed at {crossAlt:F0}ft, constraint={restriction} [{status}]");
            Assert.True(met, $"{fix.Name}: crossed at {crossAlt:F0}ft but constraint is {restriction}");
        }
    }

    private static bool IsConstraintMet(double altitude, CifpAltitudeRestriction restriction)
    {
        const double tolerance = 150;
        return restriction.Type switch
        {
            CifpAltitudeRestrictionType.At => Math.Abs(altitude - restriction.Altitude1Ft) <= tolerance,
            CifpAltitudeRestrictionType.AtOrAbove => altitude >= restriction.Altitude1Ft - tolerance,
            CifpAltitudeRestrictionType.AtOrBelow => altitude <= restriction.Altitude1Ft + tolerance,
            CifpAltitudeRestrictionType.Between => altitude >= (restriction.Altitude2Ft ?? restriction.Altitude1Ft) - tolerance
                && altitude <= restriction.Altitude1Ft + tolerance,
            _ => true,
        };
    }
}

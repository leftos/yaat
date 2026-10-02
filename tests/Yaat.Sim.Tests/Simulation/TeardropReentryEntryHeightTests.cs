using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A wrong-side arrival of a jet/turboprop joins the pattern through a
/// <see cref="MidfieldCrossingPhase"/> and a <see cref="TeardropReentryPhase"/>. The crossing is flown at
/// the AIM 4-3-3.a.2 entry height — the higher of pattern altitude and field + 1,500 ft — and the teardrop
/// exists to <em>shed</em> that entry height back down to TPA on the outbound-then-45° re-entry
/// (TeardropReentryPhase class doc; PatternBuilder.BuildFieldCrossingPrefix).
///
/// At an <em>unauthored</em> field the turbine TPA is itself field + 1,500 ft, so the crossing is flown at
/// TPA (MidfieldCrossingPhase doc-comment). There is no entry height to shed. But the teardrop's first
/// waypoint carries an unconditional `At TPA + 250` restriction (TeardropReentryPhase.cs:102), so the
/// aircraft is commanded to CLIMB 250 ft above pattern altitude and then descend back — an unexpected
/// pattern maneuver (AIM 4-3-5) that contradicts the phase's own "descend to pattern altitude" contract.
/// </summary>
public class TeardropReentryEntryHeightTests
{
    public TeardropReentryEntryHeightTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData(AircraftCategory.Turboprop, "DH8D")]
    [InlineData(AircraftCategory.Jet, "CRJ2")]
    public void TeardropReentry_AtUnauthoredField_NeverCommandsAClimbAboveTheCrossingAltitude(AircraftCategory category, string aircraftType)
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        if (rwy is null)
        {
            return;
        }

        // A field with no authored pattern altitude: the turbine TPA defaults to field + 1,500 ft.
        PatternWaypoints wp = PatternGeometry.Compute(rwy, category, aircraftType, 0, PatternDirection.Right, null, null, null, authoredRunway: null);

        // The altitude the MidfieldCrossingPhase actually hands the teardrop (crossAtPatternAltitude is
        // false here — this is exactly the gate PatternBuilder uses to decide a teardrop is inserted).
        double crossingAlt = MidfieldCrossingPhase.ResolveCrossingAltitude(
            crossAtPatternAltitude: false,
            category,
            altitudeOverrideFt: null,
            wp.PatternAltitude,
            rwy.AirportElevationFt
        );

        // Sanity: at an unauthored field the turbine crossing is flown at TPA, so there is nothing to shed.
        Assert.Equal(wp.PatternAltitude, crossingAlt, 1);

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = aircraftType,
            Position = new LatLon(rwy.AirportElevationFt, 0),
            TrueHeading = new TrueHeading(wp.DownwindHeading.Degrees),
            Altitude = crossingAlt,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            Phases = new PhaseList(),
        };
        ac.Position = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon);

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = category,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.AirportElevationFt,
            Logger = NullLogger.Instance,
        };

        var teardrop = new TeardropReentryPhase { Waypoints = wp };
        teardrop.OnStart(ctx);

        foreach (NavigationTarget target in ac.Targets.NavigationRoute)
        {
            if (target.AltitudeRestriction is not { } restriction)
            {
                continue;
            }

            Assert.True(
                restriction.Altitude1Ft <= crossingAlt + 1,
                $"Teardrop waypoint {target.Name} commands {restriction.Altitude1Ft} ft — a climb above the {crossingAlt:F0} ft crossing altitude the aircraft entered at. The teardrop must only descend toward TPA."
            );
        }
    }
}

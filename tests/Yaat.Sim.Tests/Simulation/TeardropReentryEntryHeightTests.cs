using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
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
/// TPA (MidfieldCrossingPhase doc-comment). There is no entry height to shed, so no teardrop is inserted at
/// all and the aircraft turns straight onto the downwind (PatternBuilder.BuildFieldCrossingPrefix gates on
/// the resolved crossing altitude, not the category). A teardrop flown level at TPA would otherwise send the
/// aircraft 2.5–3 nm outbound against the 45° entry flow at entrants' altitude; and its first waypoint used
/// to carry an unconditional `At TPA + 250` restriction (TeardropReentryPhase.OnStart), commanding a CLIMB
/// 250 ft above pattern altitude — the phase's contract is to descend to pattern altitude, and climbing
/// above the circuit contradicts AIM 4-3-3.a's recommendation that pattern altitude be maintained.
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
        Assert.NotNull(rwy);

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
            Position = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon),
            TrueHeading = new TrueHeading(wp.DownwindHeading.Degrees),
            Altitude = crossingAlt,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            Phases = new PhaseList(),
        };

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

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        Assert.Equal(3, route.Count);
        Assert.Equal(3, route.Count(target => target.AltitudeRestriction is not null));

        foreach (NavigationTarget target in route)
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

    [Theory]
    [InlineData(AircraftCategory.Jet, "B738")]
    [InlineData(AircraftCategory.Turboprop, "DH8D")]
    public void WrongSideTurbineJoin_AtUnauthoredField_GetsNoTeardrop_AndRejoinsDownwindTrack(AircraftCategory category, string aircraftType)
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy);

        // No authored pattern altitude, so the turbine TPA is field + 1,500 ft — exactly the AIM 4-3-3.a.2
        // entry crossing height, which leaves the teardrop nothing to shed.
        PatternWaypoints wp = PatternGeometry.Compute(rwy, category, aircraftType, 0, PatternDirection.Right, null, null, null, authoredRunway: null);

        var crossing = new MidfieldCrossingPhase { Waypoints = wp, CrossAtPatternAltitude = false };
        var downwind = new DownwindPhase { Waypoints = wp };

        List<Phase> prefix = PatternBuilder.BuildFieldCrossingPrefix(
            crossing,
            wp,
            category,
            altitudeOverrideFt: null,
            airportElevationFt: rwy.AirportElevationFt,
            [downwind]
        );

        Assert.DoesNotContain(prefix, p => p is TeardropReentryPhase);
        Assert.True(
            downwind.RejoinTrack,
            "A crossing already at pattern altitude drops straight onto the downwind, which must re-intercept its computed track."
        );
    }

    /// <summary>
    /// An authored pattern the way the airport map carries one: a <see cref="GroundRunway"/> with a
    /// <see cref="GroundRunway.PatternAltitudeAglFt"/>, resolved by
    /// <see cref="PatternGeometry.ResolveAuthoredOverrides"/> exactly as <c>PatternCommandHandler</c> does.
    /// </summary>
    private static GroundRunway MakeAuthoredPattern(double patternAltitudeAglFt) =>
        new()
        {
            Name = "28R - 10L",
            Coordinates = [],
            WidthFt = 150,
            PatternAltitudeAglFt = patternAltitudeAglFt,
            PatternSizeNm = null,
        };

    [Theory]
    [InlineData(AircraftCategory.Jet, "B738")]
    [InlineData(AircraftCategory.Turboprop, "DH8D")]
    public void WrongSideTurbineJoin_AtAuthoredLowPattern_StillGetsTeardrop(AircraftCategory category, string aircraftType)
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy);

        // An authored pattern 600 ft AGL: the turbine TPA is 1,100 AGL and the AIM 4-3-3.a.2 crossing height
        // (field + 1,500 ft) sits 400 ft above it, so the teardrop still has an entry height to shed.
        GroundRunway authored = MakeAuthoredPattern(600);
        (double? _, double? patternAltitudeMsl) = PatternGeometry.ResolveAuthoredOverrides(
            rwy,
            authored,
            category,
            commandSizeNm: null,
            commandAltitudeMslFt: null
        );
        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            category,
            aircraftType,
            0,
            PatternDirection.Right,
            null,
            patternAltitudeMsl,
            null,
            authoredRunway: authored
        );

        var crossing = new MidfieldCrossingPhase { Waypoints = wp, CrossAtPatternAltitude = false };
        var downwind = new DownwindPhase { Waypoints = wp };

        List<Phase> prefix = PatternBuilder.BuildFieldCrossingPrefix(
            crossing,
            wp,
            category,
            altitudeOverrideFt: null,
            airportElevationFt: rwy.AirportElevationFt,
            [downwind]
        );

        Assert.Contains(prefix, p => p is TeardropReentryPhase);
    }

    /// <summary>
    /// The cap in <see cref="TeardropReentryPhase.OnStart"/> binds only where the crossing is above TPA but
    /// inside the +250 ft the uncapped anchor restriction commanded. An authored 900 ft AGL pattern puts a
    /// turbine's TPA at 1,400 AGL and its AIM 4-3-3.a.2 crossing at 1,500 AGL — 100 ft above the circuit —
    /// so the teardrop is inserted and its first waypoint is capped at the crossing altitude instead of
    /// commanding a 150 ft climb above it, while the lead-in and abeam keep their authored step-down.
    /// </summary>
    [Theory]
    [InlineData(AircraftCategory.Jet, "B738")]
    [InlineData(AircraftCategory.Turboprop, "DH8D")]
    public void TeardropReentry_AtAuthoredPatternInsideTheCap_DescendsFromTheCrossingAltitude(AircraftCategory category, string aircraftType)
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy);

        GroundRunway authored = MakeAuthoredPattern(900);
        (double? _, double? patternAltitudeMsl) = PatternGeometry.ResolveAuthoredOverrides(
            rwy,
            authored,
            category,
            commandSizeNm: null,
            commandAltitudeMslFt: null
        );
        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            category,
            aircraftType,
            0,
            PatternDirection.Right,
            null,
            patternAltitudeMsl,
            null,
            authoredRunway: authored
        );

        var downwind = new DownwindPhase { Waypoints = wp };
        var crossing = new MidfieldCrossingPhase { Waypoints = wp, CrossAtPatternAltitude = false };
        List<Phase> prefix = PatternBuilder.BuildFieldCrossingPrefix(
            crossing,
            wp,
            category,
            altitudeOverrideFt: null,
            airportElevationFt: rwy.AirportElevationFt,
            [downwind]
        );
        Assert.Contains(prefix, p => p is TeardropReentryPhase);

        double crossingAlt = MidfieldCrossingPhase.ResolveCrossingAltitude(
            crossAtPatternAltitude: false,
            category,
            altitudeOverrideFt: null,
            wp.PatternAltitude,
            rwy.AirportElevationFt
        );
        Assert.Equal(100.0, crossingAlt - wp.PatternAltitude, 1);

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = aircraftType,
            Position = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon),
            TrueHeading = new TrueHeading(wp.DownwindHeading.Degrees),
            Altitude = crossingAlt,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            Phases = new PhaseList(),
        };

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

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        Assert.Equal(3, route.Count);
        int anchorAlt = route[0].AltitudeRestriction!.Altitude1Ft;
        int leadInAlt = route[1].AltitudeRestriction!.Altitude1Ft;
        int abeamAlt = route[2].AltitudeRestriction!.Altitude1Ft;
        int tpa = (int)wp.PatternAltitude;

        Assert.Equal((int)crossingAlt, anchorAlt);
        Assert.True(anchorAlt < tpa + 250, $"the uncapped anchor would have commanded {tpa + 250} ft, above the {crossingAlt:F0} ft crossing");
        Assert.Equal(tpa + 50, leadInAlt);
        Assert.Equal(tpa, abeamAlt);
        Assert.True(anchorAlt >= leadInAlt, $"anchor ({anchorAlt}) should not be below lead-in ({leadInAlt})");
        Assert.True(leadInAlt >= abeamAlt, $"lead-in ({leadInAlt}) should not be below abeam ({abeamAlt})");
    }
}

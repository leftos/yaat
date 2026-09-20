using System.IO;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// The §5-7-1.b.4 final window and the "on final" test measure from the runway's <em>landing</em> threshold, the datum
/// <see cref="LandingThreshold.Resolve(RunwayInfo, AirportGroundLayout?)"/> resolves, not from the pavement end the nav
/// database stores. On an undisplaced runway the two are the same point and nothing here is visible; on KSJC 30L they
/// are 2,537 ft (0.42 nm) apart, which is where the rule's 5 miles and the pilot's own "on final" both move.
///
/// <para>AIM 2-3-3.h.2: the pavement behind a displaced threshold is available for takeoffs in either direction and
/// landings from the opposite direction, not for landing in this one, so an arrival still has 2,537 ft to fly when it
/// crosses the pavement end — the rule's "5 miles from the runway" is 5 miles from where it may touch down.</para>
/// </summary>
public class FinalApproachLandingDatumTests(ITestOutputHelper output)
{
    /// <summary>KSJC 12R/30L is authored <c>"threshold": "1297 - 2537"</c> — landing 30L starts 2,537 ft downfield.</summary>
    private const double DisplacementFt = 2537.0;

    /// <summary>Distance (nm) from the pavement threshold that sits inside the pavement window and outside the landing one.</summary>
    private const double BetweenTheTwoWindowsNm = 4.8;

    /// <summary>Distance (nm) from the landing threshold that is inside the window on either datum.</summary>
    private const double InsideBothWindowsNm = 4.8;

    /// <summary>The explicit ATC speed (kt) the arms assign before the gate is asked about it.</summary>
    private const double AssignedSpeedKts = 180.0;

    /// <summary>
    /// How far (kt) above that assignment the arrival is still flying. <see cref="FlightPhysics"/> nulls a target the
    /// aircraft has reached, so an arm that reads <see cref="ControlTargets.TargetSpeed"/> to tell "the gate left the
    /// assignment alone" from "the gate cancelled it" has to leave the aircraft still slowing toward it.
    /// </summary>
    private const double StillSlowingKts = 30.0;

    private static AirportGroundLayout Layout() => GeoJsonParser.Parse("SJC", File.ReadAllText(Path.Combine("TestData", "sjc.geojson")), "SJC");

    private static RunwayInfo Runway()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("KSJC", "30L");
        Assert.NotNull(runway);
        return runway;
    }

    /// <summary>
    /// An arrival cleared to land on 30L, tracking the landing course, <paramref name="fromPavementNm"/> back from the
    /// pavement threshold on the extended centerline, carrying an explicit ATC speed and the SJC layout.
    /// </summary>
    private static AircraftState ArrivalOnFinal(RunwayInfo runway, AirportGroundLayout layout, double fromPavementNm)
    {
        var pavement = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var aircraft = new AircraftState
        {
            Callsign = "SJC1",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(pavement, runway.TrueHeading.ToReciprocal(), fromPavementNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = 1700,
            IndicatedAirspeed = AssignedSpeedKts + StillSlowingKts,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway, LandingClearance = ClearanceType.ClearedToLand },
        };
        aircraft.Ground.Layout = layout;
        aircraft.Targets.TargetSpeed = AssignedSpeedKts;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        return aircraft;
    }

    private static double NmTo(AircraftState aircraft, LatLon point) => GeoMath.DistanceNm(aircraft.Position, point);

    /// <summary>
    /// <c>FlightPhysics</c>'s §5-7-1.b.4 auto-cancel holds off for the 0.42 nm between the two datums: at 4.8 nm from
    /// the pavement end the arrival is still 5.2 nm from the threshold it is landing on, so the controller's speed
    /// stands. It is released once it is 5 miles from <em>that</em> threshold.
    /// </summary>
    [Fact]
    public void AutoCancelSpeedAtFinal_UsesTheLandingThresholdForItsFiveMileWindow()
    {
        RunwayInfo runway = Runway();
        AirportGroundLayout layout = Layout();
        Assert.Equal(DisplacementFt, LandingThreshold.DisplacementFt(runway, layout));
        LatLon landing = LandingThreshold.Resolve(runway, layout);

        AircraftState outside = ArrivalOnFinal(runway, layout, BetweenTheTwoWindowsNm);
        output.WriteLine(
            $"between the windows: {NmTo(outside, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude)):F2} nm from the pavement, "
                + $"{NmTo(outside, landing):F2} nm from the landing threshold"
        );
        Assert.InRange(NmTo(outside, landing), 5.0, 5.5); // premise: outside the window on the landing datum

        FlightPhysics.Update(outside, 0.1);

        Assert.True(outside.Targets.HasExplicitSpeedCommand, "the assignment was cancelled 0.42 nm early, on the pavement datum");
        Assert.Equal(AssignedSpeedKts, Assert.IsType<double>(outside.Targets.TargetSpeed));

        AircraftState inside = ArrivalOnFinal(runway, layout, InsideBothWindowsNm - (DisplacementFt / GeoMath.FeetPerNm));
        output.WriteLine($"inside both: {NmTo(inside, landing):F2} nm from the landing threshold");
        Assert.InRange(NmTo(inside, landing), 4.5, 5.0); // premise: inside the window on the landing datum

        FlightPhysics.Update(inside, 0.1);

        Assert.False(inside.Targets.HasExplicitSpeedCommand);
        Assert.Null(inside.Targets.TargetSpeed);
        Assert.Equal(AssignedSpeedKts, Assert.IsType<double>(inside.Targets.SpeedCeiling));
    }

    /// <summary>
    /// <c>SPD</c>'s refusal reads the same window from the same datum, so the two never disagree about whether an
    /// arrival is inside the final: the speed the physics would still be flying is one the instructor may still assign.
    /// </summary>
    [Fact]
    public void Spd_FiveMileFinalRejection_UsesTheLandingThreshold()
    {
        RunwayInfo runway = Runway();
        AirportGroundLayout layout = Layout();
        LatLon landing = LandingThreshold.Resolve(runway, layout);

        AircraftState outside = ArrivalOnFinal(runway, layout, BetweenTheTwoWindowsNm);
        Assert.InRange(NmTo(outside, landing), 5.0, 5.5); // premise: outside the window on the landing datum

        // The gate reads the runway and the layout off the aircraft, so the premise above has to hold through those too.
        // It is also evaluated twice: once on the real aircraft and once on the dry-run clone, which
        // CommandDispatcher.DryRunValidate rebuilds with ctx.GroundLayout — hence the layout on the context, which is
        // what SimulationEngine.BuildDispatchContext puts there.
        RunwayInfo assigned = Assert.IsType<RunwayInfo>(outside.Phases?.AssignedRunway);
        output.WriteLine(
            $"as the gate reads it: {NmTo(outside, LandingThreshold.Resolve(assigned, outside.Ground.Layout)):F2} nm, "
                + $"displacement {LandingThreshold.DisplacementFt(assigned, outside.Ground.Layout):F0} ft, "
                + $"layout {outside.Ground.Layout?.AirportId ?? "(none)"} vs runway {assigned.AirportId}"
        );

        CommandResult accepted = CommandDispatcher.Dispatch(
            new SpeedCommand(170),
            outside,
            TestDispatch.Context(new Random(0), groundLayout: layout)
        );

        output.WriteLine($"between the windows: {accepted.Message}");
        Assert.True(accepted.Success, accepted.Message);
        Assert.Equal(170, outside.Targets.TargetSpeed);

        AircraftState inside = ArrivalOnFinal(runway, layout, InsideBothWindowsNm - (DisplacementFt / GeoMath.FeetPerNm));
        Assert.InRange(NmTo(inside, landing), 4.5, 5.0); // premise: inside the window on the landing datum

        CommandResult rejected = CommandDispatcher.Dispatch(new SpeedCommand(170), inside, TestDispatch.Context(new Random(0), groundLayout: layout));

        output.WriteLine($"inside both: {rejected.Message}");
        Assert.False(rejected.Success);
        Assert.Contains("5nm final", rejected.Message);
    }

    /// <summary>
    /// The discriminating geometry for the bearing half of the test: an aircraft over the displaced stretch itself,
    /// past the pavement end and still short of the landing threshold. The pavement threshold is <em>behind</em> it —
    /// bearing to it is the reciprocal of its track, 180° off, outside the ±90° window — while the threshold it is
    /// landing on is still ahead of it. It is on a quarter-mile final, not past the runway, and only the landing datum
    /// says so. No phase is set, because the phase shortcut would answer before the geometry is reached.
    /// </summary>
    [Fact]
    public void IsOnFinal_MeasuresBearingFromTheLandingThreshold()
    {
        RunwayInfo runway = Runway();
        AirportGroundLayout layout = Layout();
        AircraftState aircraft = OverTheDisplacedStretch(runway);

        var pavement = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double bearingToPavement = GeoMath.BearingTo(aircraft.Position, pavement);
        double bearingToLanding = GeoMath.BearingTo(aircraft.Position, LandingThreshold.Resolve(runway, layout));
        output.WriteLine(
            $"track {aircraft.TrueTrack.Degrees:F0}°, bearing to the pavement end {bearingToPavement:F0}°, "
                + $"to the landing threshold {bearingToLanding:F0}°"
        );
        Assert.True(aircraft.TrueTrack.AbsAngleTo(new TrueHeading(bearingToPavement)) > 90.0); // premise: the two datums disagree

        Assert.True(ApproachCommandHandler.IsOnFinal(aircraft, runway, layout));
    }

    /// <summary>
    /// With no ground map for the field there is no published displacement to read, so the test falls back to the
    /// pavement threshold and answers exactly as it did before the landing datum was threaded through — which on that
    /// datum means the aircraft above has already passed the runway.
    /// </summary>
    [Fact]
    public void IsOnFinal_WithNoLayout_FallsBackToThePavementThreshold()
    {
        RunwayInfo runway = Runway();
        AircraftState aircraft = OverTheDisplacedStretch(runway);

        Assert.False(ApproachCommandHandler.IsOnFinal(aircraft, runway, layout: null));
    }

    /// <summary>
    /// An aircraft on the landing course halfway along 30L's displaced stretch — past the pavement end, short of the
    /// landing threshold — at the ~60 ft a 3° glidepath puts it at there.
    /// </summary>
    private static AircraftState OverTheDisplacedStretch(RunwayInfo runway)
    {
        var pavement = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        return new AircraftState
        {
            Callsign = "SJC2",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(pavement, runway.TrueHeading, DisplacementFt / 2.0 / GeoMath.FeetPerNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = 125,
            IndicatedAirspeed = 140,
            IsOnGround = false,
        };
    }
}

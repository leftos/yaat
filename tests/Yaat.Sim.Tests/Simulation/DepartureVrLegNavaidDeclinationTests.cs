using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A CR/VR departure leg's termination radial is referenced to the facility's variation of record (P/CG RADIAL;
/// AIM 1-1-17b.5(j)(1)), never the aircraft's live declination: the live value moves the crossing point as the
/// aircraft flies, so the same charted radial would terminate the leg in a different place for every aircraft.
/// KLAX GMN7 RWY 24B opens with a VR leg off SMO — fly heading 251° to cross the SMO R-154 — so the radial
/// converts with SMO's published course declination.
/// </summary>
public class DepartureVrLegNavaidDeclinationTests
{
    private const double GroundSpeedKts = 250.0;
    private const double FineDeltaSeconds = 0.05;
    private const double LiveDeclinationOffsetDeg = 3.0;
    private const double CrossingToleranceDeg = 0.1;
    private const double StartBearingOffsetDeg = 10.0;
    private const double StartDistanceNm = 8.0;
    private const int MaxTicks = 6000;

    public DepartureVrLegNavaidDeclinationTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Runs the real GMN7 RWY 24B radial leg from 10° short of SMO's published radial with the aircraft's live
    /// declination 3° off it, and asserts the leg terminates on the published radial, not on the live one.
    /// </summary>
    [Fact]
    public void VrLeg_TerminationRadial_UsesSmoDeclinationNotTheAircrafts()
    {
        (LatLon smo, double smoDeclination, double liveDeclination, ProcedureLeg vr) = Gmn7RadialLeg();
        double radialMagnetic = Assert.NotNull(vr.TerminationRadialMagnetic);
        double radialTruePublished = new MagneticHeading(radialMagnetic).ToTrue(smoDeclination).Degrees;
        double radialTrueLive = new MagneticHeading(radialMagnetic).ToTrue(liveDeclination).Degrees;

        double crossedBearing = CrossingBearing(vr, smo, radialTruePublished, liveDeclination);
        double offPublished = new TrueHeading(crossedBearing).AbsAngleTo(new TrueHeading(radialTruePublished));
        double offLive = new TrueHeading(crossedBearing).AbsAngleTo(new TrueHeading(radialTrueLive));

        Assert.True(
            offLive > LiveDeclinationOffsetDeg - 1.0,
            $"setup: the crossing at {crossedBearing:F2}T sits only {offLive:F2}° from the live-declination radial "
                + $"({radialTrueLive:F2}T), so the two answers are not distinguishable"
        );
        Assert.True(
            offPublished < CrossingToleranceDeg,
            $"the VR leg terminated at bearing {crossedBearing:F2}T from SMO, {offPublished:F2}° off the published "
                + $"radial (SMO R-{radialMagnetic:F0} at SMO's {smoDeclination:F1}° published declination = "
                + $"{radialTruePublished:F2}T); the aircraft's live {liveDeclination:F1}° declination puts that radial at "
                + $"{radialTrueLive:F2}T, and the leg crossed there instead"
        );
    }

    /// <summary>A resolved radial leg's published declination survives the snapshot round trip.</summary>
    [Fact]
    public void RadialLeg_PublishedDeclination_RoundTripsThroughSnapshot()
    {
        ProcedureLeg vr = Gmn7RadialLeg().Leg;
        double published = Assert.NotNull(vr.RadialDeclinationDeg);

        var restored = ProcedureLeg.FromSnapshot(vr.ToSnapshot());

        Assert.Equal(published, Assert.NotNull(restored.RadialDeclinationDeg), precision: 9);
    }

    /// <summary>
    /// A leg restored from a pre-V43 snapshot carries no declination — nothing recorded its navaid then — so it falls
    /// back to the aircraft's live declination and crosses exactly where that snapshot's flight crossed.
    /// </summary>
    [Fact]
    public void PreV43SnapshotLeg_RestoresWithoutDeclination_AndCrossesOnTheLiveRadial()
    {
        (LatLon smo, double smoDeclination, double liveDeclination, ProcedureLeg vr) = Gmn7RadialLeg();
        double radialMagnetic = Assert.NotNull(vr.TerminationRadialMagnetic);
        double radialTruePublished = new MagneticHeading(radialMagnetic).ToTrue(smoDeclination).Degrees;
        double radialTrueLive = new MagneticHeading(radialMagnetic).ToTrue(liveDeclination).Degrees;

        var legacy = ProcedureLeg.FromSnapshot(WithoutPublishedDeclination(vr.ToSnapshot()));
        Assert.Null(legacy.RadialDeclinationDeg);

        double crossedBearing = CrossingBearing(legacy, smo, radialTruePublished, liveDeclination);
        double offLive = new TrueHeading(crossedBearing).AbsAngleTo(new TrueHeading(radialTrueLive));
        double offPublished = new TrueHeading(crossedBearing).AbsAngleTo(new TrueHeading(radialTruePublished));

        Assert.True(
            offLive < CrossingToleranceDeg,
            $"a leg restored from a pre-V43 snapshot must keep its live-declination crossing at {radialTrueLive:F2}T, "
                + $"but it sequenced at {crossedBearing:F2}T ({offLive:F2}° off)"
        );
        Assert.True(
            offPublished > LiveDeclinationOffsetDeg - 1.0,
            $"setup: the crossing at {crossedBearing:F2}T is {offPublished:F2}° from the published radial "
                + $"({radialTruePublished:F2}T), so the replay did not use the live value"
        );
    }

    /// <summary>The DTO a pre-V43 snapshot wrote for this leg: the same JSON with the new member absent.</summary>
    private static ProcedureLegDto WithoutPublishedDeclination(ProcedureLegDto dto)
    {
        JsonObject node = JsonNode.Parse(JsonSerializer.Serialize(dto))!.AsObject();
        Assert.True(node.Remove("RadialDeclinationDeg"), "setup: the serialized leg must carry the published declination");
        return node.Deserialize<ProcedureLegDto>()!;
    }

    /// <summary>The SMO position, its published declination, a live declination 3° off it, and GMN7 RWY 24B's radial leg.</summary>
    private static (LatLon Smo, double SmoDeclination, double LiveDeclination, ProcedureLeg Leg) Gmn7RadialLeg()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon) smoRaw = Assert.NotNull(navDb.GetFixPosition("SMO"));
        var smo = new LatLon(smoRaw.Lat, smoRaw.Lon);
        double smoDeclination = navDb.GetPublishedCourseDeclination("SMO", string.Empty, smo);

        ProcedureLeg vr = Assert.Single(Gmn7CodedLegs(), l => l.Type == ProcedureLegType.HeadingToRadial);
        Assert.Equal(154.0, Assert.NotNull(vr.TerminationRadialMagnetic), precision: 3);
        return (smo, smoDeclination, smoDeclination + LiveDeclinationOffsetDeg, vr);
    }

    /// <summary>
    /// Flies <paramref name="leg"/> on the course its pilot would steer from 10° short of the published radial, moving
    /// the aircraft by hand so the live declination is an input the test owns, and returns the bearing from SMO at
    /// which the phase sequenced the leg.
    /// </summary>
    private static double CrossingBearing(ProcedureLeg leg, LatLon smo, double radialTruePublished, double liveDeclination)
    {
        double courseTrue = new MagneticHeading(Assert.NotNull(leg.CourseMagnetic)).ToTrue(liveDeclination).Degrees;
        var ac = new AircraftState
        {
            Callsign = "SWA1822",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(smo, new TrueHeading(radialTruePublished - StartBearingOffsetDeg), StartDistanceNm),
            TrueHeading = new TrueHeading(courseTrue),
            Altitude = 3500,
            IsOnGround = false,
            Declination = liveDeclination,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KLAX",
                Destination = "KSAN",
                Route = "GMN7",
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList(),
        };
        var phase = new DepartureProcedurePhase { Legs = [leg], PostRoute = [] };
        ac.Phases.Add(phase);

        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = FineDeltaSeconds,
            FieldElevation = 125,
            Logger = NullLogger.Instance,
        };

        for (int i = 0; i < MaxTicks; i++)
        {
            ac.Declination = liveDeclination;
            PhaseRunner.Tick(ac, ctx);
            if (phase.ActiveLegIndex > 0)
            {
                return GeoMath.BearingTo(smo, ac.Position);
            }
            ac.Position = GeoMath.ProjectPoint(ac.Position, new TrueHeading(courseTrue), (GroundSpeedKts / 3600.0) * FineDeltaSeconds);
        }

        throw new InvalidOperationException($"the radial leg never sequenced within {MaxTicks} ticks of {FineDeltaSeconds}s");
    }

    /// <summary>The coded leading legs of KLAX GMN7 RWY 24B, resolved from the pinned CIFP the way a departure flies them.</summary>
    private static List<ProcedureLeg> Gmn7CodedLegs()
    {
        CifpSidProcedure? sid = NavigationDatabase.Instance.GetSid("KLAX", "GMN7");
        Assert.NotNull(sid);
        Assert.True(
            sid.RunwayTransitions.TryGetValue("RW24B", out CifpTransition? runway),
            "precondition: KLAX GMN7 must carry the RW24B runway transition"
        );
        List<ProcedureLeg>? coded = ProcedureLegResolver.ExtractActiveDepartureLegs(ProcedureLegResolver.Resolve(runway.Legs));
        Assert.NotNull(coded);
        return coded;
    }
}

using System.Globalization;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// <see cref="ScenarioExporter"/> turns the aircraft in a room back into a scenario the loader can restart: a stand becomes
/// a <c>Parking</c> start, a final to the destination an <c>OnFinal</c> start, an IFR aircraft on its route a
/// <c>Coordinates</c> start carrying the rest of that route — and everything the loader cannot restart in the same
/// situation is flagged for the author.
/// </summary>
public class ScenarioExporterTests
{
    private const string Stand = "A4";

    private static readonly TestAirportGroundData GroundData = new();

    private static readonly ScenarioExportContext Context = new(
        "ZOA",
        "SFO",
        "2B",
        new DateTime(2026, 9, 23, 14, 5, 0, DateTimeKind.Utc),
        MagneticDeclination.EvaluationDateUtc
    );

    public ScenarioExporterTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ParkedAircraft_ExportsAsParking_NotFlagged()
    {
        AircraftState parked = LoadSingle(ParkedJson("UAL123", "KLAX"));

        ScenarioExportResult result = Export(parked);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("Parking", ac.StartingConditions.Type);
        Assert.Equal(Stand, ac.StartingConditions.Parking);
        Assert.Equal("SFO", ac.AirportId);
        Assert.Empty(result.Flags);
        Assert.Equal("SFO snapshot 2026-09-23 1405", result.Scenario.Name);
        Assert.Equal("ZOA", result.Scenario.ArtccId);
        Assert.Equal("2B", result.Scenario.StudentPositionId);
    }

    [Fact]
    public void Simulated_TaxiingPastStandNode_NotParking()
    {
        AircraftState rolling = LoadSingle(ParkedJson("UAL123", "KLAX"));
        rolling.IndicatedAirspeed = 12;

        ScenarioExportResult result = Export(rolling);

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("UAL123", ScenarioExporter.ReasonNotAtStand), Assert.Single(result.Flags));
    }

    [Fact]
    public void Shadow_ParkedWithin75Ft_Parking()
    {
        LatLon stand = StandNode(SfoLayout()).Position;
        LatLon nearStand = GeoMath.ProjectPoint(stand, new TrueHeading(90), 50.0 / GeoMath.FeetPerNm);
        AircraftState shadow = Shadow("SKW55", GroundSample(nearStand, 0), null);

        ScenarioExportResult result = Export(shadow, null);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("Parking", ac.StartingConditions.Type);
        Assert.Equal(Stand, ac.StartingConditions.Parking);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void ParkedAtDestination_FlaggedArrived()
    {
        AircraftState arrived = LoadSingle(ParkedJson("UAL123", "KSFO"));

        ScenarioExportResult result = Export(arrived);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("Parking", ac.StartingConditions.Type);
        Assert.Equal(Stand, ac.StartingConditions.Parking);
        Assert.Equal(new ScenarioExportFlag("UAL123", ScenarioExporter.ReasonArrived), Assert.Single(result.Flags));
    }

    [Fact]
    public void LinedUpOnRunway_ExportsOnRunway()
    {
        AircraftState linedUp = LoadSingle(OnRunwayJson("UAL5", "SFO", "28L"));
        Assert.IsType<LinedUpAndWaitingPhase>(linedUp.Phases?.CurrentPhase);

        ScenarioExportResult result = Export(linedUp);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("OnRunway", ac.StartingConditions.Type);
        Assert.Equal("28L", ac.StartingConditions.Runway);
        Assert.Equal("SFO", ac.AirportId);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void HoldingShort_Flagged()
    {
        AircraftState holding = LoadSingle(GroundCoordinatesJson("SWA7", MidTaxiway(SfoLayout())));
        holding.Phases = new PhaseList();
        holding.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28L",
                }
            )
        );

        ScenarioExportResult result = Export(holding);

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("SWA7", ScenarioExporter.ReasonHoldingShort), Assert.Single(result.Flags));
    }

    [Fact]
    public void GroundShadow_NoLayout_ExportsCoordinatesFlaggedNotAtStand()
    {
        LatLon stand = StandNode(SfoLayout()).Position;
        AircraftState shadow = Shadow("SKW55", GroundSample(stand, 0), null);

        ScenarioExportResult result = ScenarioExporter.Export([shadow], Context, null, _ => null);

        StartingConditions cond = Assert.Single(result.Scenario.Aircraft).StartingConditions;
        Assert.Equal("Coordinates", cond.Type);
        Assert.NotNull(cond.Coordinates);
        Assert.Equal(stand.Lat, cond.Coordinates.Lat, 6);
        Assert.Null(cond.Altitude);
        Assert.Equal(new ScenarioExportFlag("SKW55", ScenarioExporter.ReasonNotAtStand), Assert.Single(result.Flags));
    }

    [Fact]
    public void Shadow_PlanWithEmptyRoute_FlaggedNoFiledRoute()
    {
        AircraftState shadow = AirborneShadow("SKW55", new LatLon(37.9, -121.6));
        var plan = new ScenarioFlightPlan
        {
            Rules = "IFR",
            Departure = "KSMF",
            Destination = "KLAX",
            Route = "",
        };

        ScenarioExportResult result = Export(shadow, plan);

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("SKW55", ScenarioExporter.ReasonNoFiledRoute), Assert.Single(result.Flags));
    }

    [Theory]
    [InlineData("KOAK")]
    [InlineData("OAK")]
    public void Destination_KOAKandOAK_BothMatch(string destination)
    {
        AircraftState onFinal = LoadSingle(OnFinalJson("AAL9", "OAK", "28R", 6.0, destination));

        ScenarioExportResult result = Export(onFinal);

        Assert.Equal("OnFinal", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void TaxiingAircraft_ExportsAsGroundCoordinates_Flagged()
    {
        AirportGroundLayout layout = SfoLayout();
        AircraftState taxiing = LoadSingle(GroundCoordinatesJson("SWA7", MidTaxiway(layout)));
        taxiing.Phases!.Start(CommandDispatcher.BuildMinimalContext(taxiing));
        CommandResult taxi = GroundCommandHandler.TryTaxiAuto(taxiing, new TaxiAutoCommand("28L", null, null), layout);
        Assert.True(taxi.Success, $"TAXIAUTO 28L should succeed: {taxi.Message}");
        Assert.IsType<TaxiingPhase>(taxiing.Phases.CurrentPhase);

        ScenarioExportResult result = Export(taxiing);

        StartingConditions cond = Assert.Single(result.Scenario.Aircraft).StartingConditions;
        Assert.Equal("Coordinates", cond.Type);
        Assert.Null(cond.Altitude);
        Assert.Null(cond.Speed);
        Assert.NotNull(cond.Coordinates);
        Assert.True(GeoMath.DistanceNm(taxiing.Position, new LatLon(cond.Coordinates.Lat, cond.Coordinates.Lon)) < 0.001);
        Assert.Equal(new ScenarioExportFlag("SWA7", ScenarioExporter.ReasonNotAtStand), Assert.Single(result.Flags));
    }

    [Fact]
    public void IfrEnRouteAircraft_ExportsRemainingRoute_NotFlagged()
    {
        AircraftState enRoute = LoadSingle(AirborneJson("DAL45", "IFR", "SUNOL ECA"));
        Assert.NotEmpty(enRoute.Targets.NavigationRoute);

        ScenarioExportResult result = Export(enRoute);

        StartingConditions cond = Assert.Single(result.Scenario.Aircraft).StartingConditions;
        Assert.Equal("Coordinates", cond.Type);
        Assert.Equal(string.Join(' ', enRoute.Targets.NavigationRoute.Select(t => t.Name)), cond.NavigationPath);
        Assert.Equal(11000, cond.Altitude);
        Assert.Equal(250, cond.Speed);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void VectoredIfrAircraft_ExportsCoordinates_FlaggedOffRoute()
    {
        AircraftState vectored = LoadSingle(AirborneJson("DAL45", "IFR", "SUNOL ECA"));
        vectored.Targets.AssignedMagneticHeading = new MagneticHeading(270);

        ScenarioExportResult result = Export(vectored);

        StartingConditions cond = Assert.Single(result.Scenario.Aircraft).StartingConditions;
        Assert.Equal("Coordinates", cond.Type);
        Assert.Null(cond.NavigationPath);
        Assert.Equal(11000, cond.Altitude);
        Assert.Equal(new ScenarioExportFlag("DAL45", ScenarioExporter.ReasonOffRoute), Assert.Single(result.Flags));
    }

    [Fact]
    public void AircraftOnFinalToDestination_ExportsOnFinal_WithRunwayAndDistance()
    {
        AircraftState onFinal = LoadSingle(OnFinalJson("AAL9", "OAK", "28R", 6.0, "KOAK"));

        ScenarioExportResult result = Export(onFinal);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("OnFinal", ac.StartingConditions.Type);
        Assert.Equal("28R", ac.StartingConditions.Runway);
        Assert.Equal("OAK", ac.AirportId);
        Assert.NotNull(ac.StartingConditions.DistanceFromRunway);
        Assert.InRange(ac.StartingConditions.DistanceFromRunway.Value, 5.95, 6.05);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void AircraftOnFinalToNonDestinationRunway_IsFlagged()
    {
        AircraftState onFinal = LoadSingle(OnFinalJson("AAL9", "OAK", "28R", 6.0, "KSFO"));

        ScenarioExportResult result = Export(onFinal);

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("AAL9", ScenarioExporter.ReasonFinalNotDestination), Assert.Single(result.Flags));
    }

    [Fact]
    public void VfrAirborne_IsFlagged()
    {
        AircraftState vfr = LoadSingle(AirborneJson("N123AB", "VFR", ""));

        ScenarioExportResult result = Export(vfr);

        StartingConditions cond = Assert.Single(result.Scenario.Aircraft).StartingConditions;
        Assert.Equal("Coordinates", cond.Type);
        Assert.Equal(11000, cond.Altitude);
        Assert.Equal(new ScenarioExportFlag("N123AB", ScenarioExporter.ReasonAirborneVfr), Assert.Single(result.Flags));
    }

    [Fact]
    public void ShadowOnFinal_DetectedGeometrically()
    {
        RunwayInfo runway = Oak28R();
        AircraftState shadow = ShadowOnExtendedCentreline(runway, 8.0, 0.0);

        ScenarioExportResult result = Export(shadow, OaklandArrivalPlan());

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("OnFinal", ac.StartingConditions.Type);
        Assert.Equal("28R", ac.StartingConditions.Runway);
        Assert.NotNull(ac.StartingConditions.DistanceFromRunway);
        Assert.InRange(ac.StartingConditions.DistanceFromRunway.Value, 7.9, 8.1);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void ShadowJustOutside15Nm_IsNotOnFinal()
    {
        AircraftState shadow = ShadowOnExtendedCentreline(Oak28R(), ScenarioExporter.ShadowFinalMaxDistanceNm + 0.3, 0.0);

        ScenarioExportResult result = Export(shadow, OaklandArrivalPlan());

        Assert.NotEqual("OnFinal", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
    }

    [Fact]
    public void ShadowOffCentrelineBeyond1Nm_IsNotOnFinal()
    {
        AircraftState shadow = ShadowOnExtendedCentreline(Oak28R(), 8.0, ScenarioExporter.ShadowFinalMaxCrossTrackNm + 0.2);

        ScenarioExportResult result = Export(shadow, OaklandArrivalPlan());

        Assert.NotEqual("OnFinal", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
    }

    [Fact]
    public void Shadow_HighAboveGlidepathDescending_NotOnFinal()
    {
        RunwayInfo runway = Oak28R();
        AircraftState shadow = ShadowOnFinalAt(runway, 8.0, 0.0, GlidepathAltitude(runway, 8.0) + 2500, -1500);

        ScenarioExportResult result = Export(shadow, OaklandArrivalPlan());

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("SWA1234", ScenarioExporter.ReasonOffGlidepath), Assert.Single(result.Flags));
    }

    [Fact]
    public void Shadow_LowAlignedClimbing_NotOnFinal()
    {
        RunwayInfo runway = Oak28R();
        AircraftState goingAround = ShadowOnFinalAt(runway, 2.0, 0.0, GlidepathAltitude(runway, 2.0), 1500);

        ScenarioExportResult result = Export(goingAround, OaklandArrivalPlan());

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("SWA1234", ScenarioExporter.ReasonOffGlidepath), Assert.Single(result.Flags));
    }

    [Fact]
    public void Shadow_LevelBelowGlidepathOnLocalizer_OnFinal()
    {
        RunwayInfo runway = Oak28R();
        AircraftState intercepting = ShadowOnFinalAt(runway, 10.0, 0.3, GlidepathAltitude(runway, 10.0) - 800, 0);

        ScenarioExportResult result = Export(intercepting, OaklandArrivalPlan());

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("OnFinal", ac.StartingConditions.Type);
        Assert.Equal("28R", ac.StartingConditions.Runway);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Simulated_OverThreshold_FlaggedNotOnFinal()
    {
        AircraftState landing = LoadSingle(OnFinalJson("AAL9", "OAK", "28R", 0.1, "KOAK"));
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(landing);
        landing.Phases!.Start(ctx);
        landing.Phases.SkipTo<LandingPhase>(ctx);
        Assert.IsType<LandingPhase>(landing.Phases.CurrentPhase);

        ScenarioExportResult result = Export(landing);

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("AAL9", ScenarioExporter.ReasonOverThreshold), Assert.Single(result.Flags));
    }

    [Fact]
    public void Simulated_PatternFinalNoFlightPlan_ExportsOnFinal()
    {
        AircraftState onFinal = LoadSingle(OnFinalNoPlanJson("N172SP", "OAK", "28R", 1.5));
        Assert.False(onFinal.FlightPlan.HasFlightPlan);

        ScenarioExportResult result = Export(onFinal);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("OnFinal", ac.StartingConditions.Type);
        Assert.Equal("28R", ac.StartingConditions.Runway);
        Assert.Equal("OAK", ac.AirportId);
        Assert.Null(ac.FlightPlan);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Shadow_MidRoute_RouteTrimmedToFixesAhead()
    {
        LatLon sunol = FixPosition("SUNOL");
        double trackDeg = GeoMath.BearingTo(sunol, FixPosition("MOD"));
        LatLon pastSunol = GeoMath.ProjectPoint(sunol, new TrueHeading(trackDeg), 5.0);
        AircraftState shadow = Shadow("SKW55", AirSample(pastSunol, 12000, 280, trackDeg, 0), null);
        var plan = new ScenarioFlightPlan
        {
            Rules = "IFR",
            Departure = "KSFO",
            Destination = "KSAC",
            Route = "OSI SUNOL MOD",
        };

        ScenarioExportResult result = Export(shadow, plan);

        Assert.Equal("MOD", Assert.Single(result.Scenario.Aircraft).StartingConditions.NavigationPath);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Ifr_InHold_FlaggedProcedure()
    {
        AircraftState holding = LoadSingle(AirborneJson("DAL45", "IFR", "SUNOL ECA"));
        Dispatch(holding, "HOLD SUNOL 090 10 RIGHT");
        Assert.NotNull(holding.Phases?.CurrentPhase);

        ScenarioExportResult result = Export(holding);

        Assert.Equal("Coordinates", Assert.Single(result.Scenario.Aircraft).StartingConditions.Type);
        Assert.Equal(new ScenarioExportFlag("DAL45", ScenarioExporter.ReasonInProcedure), Assert.Single(result.Flags));
    }

    [Fact]
    public void Ifr_Descending_ExportsDmPreset_RoundTrips()
    {
        AircraftState descending = LoadSingle(AirborneJson("DAL45", "IFR", "SUNOL ECA"));
        Dispatch(descending, "DM 050");

        ScenarioExportResult result = Export(descending);

        Assert.Equal(["DM 050"], Assert.Single(result.Scenario.Aircraft).PresetCommands.Select(preset => preset.Command));
        AircraftState reloaded = ReloadWithPresets(result);
        Assert.Equal(5000, reloaded.Targets.AssignedAltitude);
        Assert.Equal(descending.Targets.AssignedAltitude, reloaded.Targets.AssignedAltitude);
        Assert.Equal(descending.Targets.TargetAltitude, reloaded.Targets.TargetAltitude);
    }

    [Theory]
    [InlineData("SPD 210")]
    [InlineData("SPD 210+")]
    [InlineData("SPD 210-")]
    public void Ifr_AssignedSpeed_ExportsSpdPreset_RoundTrips(string command)
    {
        AircraftState slowed = LoadSingle(AirborneJson("DAL45", "IFR", "SUNOL ECA"));
        Dispatch(slowed, command);

        ScenarioExportResult result = Export(slowed);

        Assert.Equal([command], Assert.Single(result.Scenario.Aircraft).PresetCommands.Select(preset => preset.Command));
        AircraftState reloaded = ReloadWithPresets(result);
        Assert.Equal(210, reloaded.Targets.AssignedSpeed);
        Assert.Equal(slowed.Targets.AssignedSpeed, reloaded.Targets.AssignedSpeed);
        Assert.Equal(slowed.Targets.TargetSpeed, reloaded.Targets.TargetSpeed);
        Assert.Equal(slowed.Targets.SpeedFloor, reloaded.Targets.SpeedFloor);
        Assert.Equal(slowed.Targets.SpeedCeiling, reloaded.Targets.SpeedCeiling);
    }

    [Fact]
    public void Ifr_DescendVia_ExportsDviaPreset()
    {
        AircraftState arrival = OakesArrival();
        Dispatch(arrival, "DVIA 080");
        Assert.True(arrival.Procedure.StarViaMode);

        ScenarioExportResult result = Export(arrival);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Equal("Coordinates", ac.StartingConditions.Type);
        Assert.Equal(["DVIA 080"], ac.PresetCommands.Select(preset => preset.Command));
    }

    [Fact]
    public void ShadowWithSuppliedFlightPlan_ExportsIt()
    {
        AircraftState shadow = AirborneShadow("SKW55", new LatLon(37.9, -121.6));
        var plan = new ScenarioFlightPlan
        {
            Rules = "IFR",
            Departure = "KSMF",
            Destination = "KLAX",
            CruiseAltitude = 33000,
            CruiseSpeed = 450,
            Route = "SUNOL MOD",
            AircraftType = "CRJ7/L",
        };

        ScenarioExportResult result = Export(shadow, plan);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Same(plan, ac.FlightPlan);
        Assert.Equal("Coordinates", ac.StartingConditions.Type);
        Assert.Equal("MOD", ac.StartingConditions.NavigationPath);
        Assert.Equal(12000, ac.StartingConditions.Altitude);
        Assert.Equal(Math.Round(shadow.IndicatedAirspeed), ac.StartingConditions.Speed);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Shadow_AtCruise_ExportsIndicatedNotGroundSpeed()
    {
        AircraftState shadow = AirborneShadow("SKW55", new LatLon(37.9, -121.6));
        double expectedIas = Math.Round(WindInterpolator.TasToIas(280, 12000));
        Assert.True(expectedIas < 260, $"IAS {expectedIas} at FL120 should sit well below the 280 kt ground speed");

        ScenarioExportResult result = Export(shadow, null);

        Assert.Equal(expectedIas, Assert.Single(result.Scenario.Aircraft).StartingConditions.Speed);
    }

    [Fact]
    public void Shadow_InCrosswind_ExportsHeadingNotTrack()
    {
        var position = new LatLon(37.9, -121.6);
        var crosswind = new WeatherProfile { WindLayers = [new WindLayer { Direction = 180, Speed = 40 }] };
        AircraftState shadow = Shadow("SKW55", AirSample(position, 12000, 280, 90, 0), crosswind);
        Assert.True(shadow.TrueHeading.AbsAngleTo(new TrueHeading(90)) > 5.0, "a 40 kt crosswind should crab the heading off the track");

        ScenarioExportResult result = Export(shadow, null);

        double? heading = Assert.Single(result.Scenario.Aircraft).StartingConditions.Heading;
        Assert.NotNull(heading);
        double expected = MagneticDeclination.TrueToMagnetic(shadow.TrueHeading.Degrees, position, Context.MagneticModelDateUtc);
        Assert.True(new TrueHeading(heading.Value).AbsAngleTo(new TrueHeading(expected)) < 0.2, $"exported {heading} vs heading {expected:F1}");
    }

    [Fact]
    public void ShadowWithoutFlightPlan_FlaggedNoFlightPlan()
    {
        AircraftState shadow = AirborneShadow("N456CD", new LatLon(37.9, -121.6));

        ScenarioExportResult result = Export(shadow, null);

        ScenarioAircraft ac = Assert.Single(result.Scenario.Aircraft);
        Assert.Null(ac.FlightPlan);
        Assert.Equal("Coordinates", ac.StartingConditions.Type);
        Assert.NotNull(ac.StartingConditions.Coordinates);
        Assert.Equal(37.9, ac.StartingConditions.Coordinates.Lat, 6);
        Assert.Equal(12000, ac.StartingConditions.Altitude);
        Assert.Equal(new ScenarioExportFlag("N456CD", ScenarioExporter.ReasonNoFlightPlan), Assert.Single(result.Flags));
    }

    [Fact]
    public void ExportedScenario_ReloadsThroughScenarioLoader_WithEquivalentPositions()
    {
        AirportGroundLayout layout = SfoLayout();
        List<AircraftState> originals =
        [
            LoadSingle(ParkedJson("UAL123", "KLAX")),
            LoadSingle(GroundCoordinatesJson("SWA7", MidTaxiway(layout))),
            LoadSingle(AirborneJson("DAL45", "IFR", "SUNOL ECA")),
            LoadSingle(OnFinalJson("AAL9", "SFO", "28L", 7.0, "KSFO")),
        ];
        string[] expectedTypes = ["Parking", "Coordinates", "Coordinates", "OnFinal"];

        ScenarioExportResult exported = ScenarioExporter.Export(originals, Context, GroundData, _ => null);
        string json = ScenarioExporter.Serialize(exported.Scenario);
        ScenarioLoadResult reloaded = ScenarioLoader.Load(json, GroundData, new Random(0), MagneticDeclination.EvaluationDateUtc);

        Assert.Empty(reloaded.DeferredAircraft);
        Assert.Equal(expectedTypes, exported.Scenario.Aircraft.Select(ac => ac.StartingConditions.Type));
        Assert.Equal(originals.Count, reloaded.ImmediateAircraft.Count);
        foreach (AircraftState original in originals)
        {
            AircraftState copy = reloaded.ImmediateAircraft.Single(loaded => loaded.State.Callsign == original.Callsign).State;
            double offsetFt = GeoMath.DistanceNm(original.Position, copy.Position) * GeoMath.FeetPerNm;
            Assert.True(offsetFt < 20.0, $"{original.Callsign} reloaded {offsetFt:F1} ft from where it was exported");
            Assert.InRange(copy.Altitude, original.Altitude - 10.0, original.Altitude + 10.0);
            Assert.Equal(original.IsOnGround, copy.IsOnGround);
            Assert.True(original.TrueHeading.AbsAngleTo(copy.TrueHeading) < 1.0, $"{original.Callsign} heading changed on reload");
        }
    }

    private static ScenarioExportResult Export(AircraftState aircraft) => ScenarioExporter.Export([aircraft], Context, GroundData, _ => null);

    private static ScenarioExportResult Export(AircraftState shadow, ScenarioFlightPlan? plan)
    {
        var plans = new Dictionary<string, ScenarioFlightPlan?> { [shadow.Callsign] = plan };
        return ScenarioExporter.Export([shadow], Context, GroundData, state => plans[state.Callsign]);
    }

    private static AircraftState LoadSingle(string json)
    {
        ScenarioLoadResult result = ScenarioLoader.Load(json, GroundData, new Random(0), MagneticDeclination.EvaluationDateUtc);
        Assert.Empty(result.DeferredAircraft);
        return Assert.Single(result.ImmediateAircraft).State;
    }

    private static void Dispatch(AircraftState aircraft, string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command, aircraft.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"{command} should parse: {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new Random(0)));
        Assert.True(result.Success, $"{command} should succeed: {result.Message}");
    }

    /// <summary>Reloads an exported single-aircraft scenario and dispatches its presets through the real parser.</summary>
    private static AircraftState ReloadWithPresets(ScenarioExportResult exported)
    {
        string json = ScenarioExporter.Serialize(exported.Scenario);
        ScenarioLoadResult reloaded = ScenarioLoader.Load(json, GroundData, new Random(0), MagneticDeclination.EvaluationDateUtc);
        LoadedAircraft loaded = Assert.Single(reloaded.ImmediateAircraft);
        foreach (PresetCommand preset in loaded.PresetCommands)
        {
            Dispatch(loaded.State, preset.Command);
        }

        return loaded.State;
    }

    private static LatLon FixPosition(string fix)
    {
        (double Lat, double Lon)? position = NavigationDatabase.Instance.GetFixPosition(fix);
        Assert.NotNull(position);
        return new LatLon(position.Value.Lat, position.Value.Lon);
    }

    /// <summary>A generated arrival established on Oakland's OAKES3 STAR, level at 16,000 with the STAR in its filed route.</summary>
    private static AircraftState OakesArrival()
    {
        CifpStarProcedure star =
            NavigationDatabase.Instance.GetStar("KOAK", "OAKES3") ?? throw new InvalidOperationException("nav data has no OAKES3");
        string entryFix = DepartureClearanceHandler.ResolveLegsToTargets([.. star.CommonLegs])[0].Name;
        string runwayKey = star.RunwayTransitions.Keys.First();
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Heavy,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = entryFix,
            StarId = "OAKES3",
            StarRunway = runwayKey.StartsWith("RW", StringComparison.Ordinal) ? runwayKey[2..] : runwayKey,
            DescendVia = false,
            StarAltitude = 16000,
            DestinationAirportId = "KOAK",
        };
        (AircraftState? state, string? error) = AircraftGenerator.Generate(
            request,
            "KOAK",
            [],
            groundLayout: null,
            new Random(1),
            new BeaconCodePool()
        );
        Assert.Null(error);
        Assert.NotNull(state);
        return state;
    }

    private static AirportGroundLayout SfoLayout()
    {
        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        Assert.NotNull(layout);
        return layout;
    }

    /// <summary>
    /// The middle of the longest straight stretch of SFO taxiway F: mid-edge, so the ground snap has one edge to choose and
    /// no junction for the reload to resolve onto a crossing taxiway instead.
    /// </summary>
    private static LatLon MidTaxiway(AirportGroundLayout layout)
    {
        GroundEdge edge = layout
            .Edges.Where(e =>
                (e.TaxiwayName == "F") && (Math.Abs(GeoMath.DistanceNm(e.Nodes[0].Position, e.Nodes[1].Position) - e.DistanceNm) < 1e-4)
            )
            .OrderByDescending(e => e.DistanceNm)
            .First();
        LatLon a = edge.Nodes[0].Position;
        LatLon b = edge.Nodes[1].Position;
        return new LatLon((a.Lat + b.Lat) / 2.0, (a.Lon + b.Lon) / 2.0);
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("nav data has no OAK 28R");

    private static ScenarioFlightPlan OaklandArrivalPlan() =>
        new()
        {
            Rules = "IFR",
            Departure = "KLAX",
            Destination = "KOAK",
            CruiseAltitude = 35000,
            Route = "SUNOL",
        };

    /// <summary>
    /// A shadow <paramref name="distanceNm"/> out on the runway's final, <paramref name="offsetNm"/> right of the
    /// centreline, on the loader's glidepath and descending.
    /// </summary>
    private static AircraftState ShadowOnExtendedCentreline(RunwayInfo runway, double distanceNm, double offsetNm) =>
        ShadowOnFinalAt(runway, distanceNm, offsetNm, GlidepathAltitude(runway, distanceNm), -700);

    private static AircraftState ShadowOnFinalAt(RunwayInfo runway, double distanceNm, double offsetNm, double altitudeFt, double vsFpm)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon onCentreline = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), distanceNm);
        LatLon position = GeoMath.ProjectPoint(onCentreline, new TrueHeading(runway.TrueHeading.Degrees + 90.0), offsetNm);
        return Shadow("SWA1234", AirSample(position, altitudeFt, 160, runway.TrueHeading.Degrees, vsFpm), null);
    }

    private static double GlidepathAltitude(RunwayInfo runway, double distanceNm) =>
        GlideSlopeGeometry.AltitudeAtDistance(distanceNm, runway.ElevationFt, AircraftCategorization.Categorize("B738"));

    private static AircraftState AirborneShadow(string callsign, LatLon position) => Shadow(callsign, AirSample(position, 12000, 280, 45, 0), null);

    private static LiveTrafficSample AirSample(LatLon position, double altitudeFt, double groundSpeedKts, double trueTrackDeg, double vsFpm) =>
        new(0, position.Lat, position.Lon, altitudeFt, groundSpeedKts, trueTrackDeg, vsFpm, LiveTrafficSource.Stars, null);

    /// <summary>
    /// A live-traffic shadow built through the feed kinematics — so its heading and IAS are what the live path derives
    /// from the sample and <paramref name="weather"/>'s wind — then with its position and altitude moved off the sample,
    /// so an export that read those physics fields instead of the sample would be caught.
    /// </summary>
    private static AircraftState Shadow(string callsign, LiveTrafficSample sample, WeatherProfile? weather)
    {
        AircraftState shadow = LiveTrafficKinematics.CreateShadow(callsign, "B738", sample, new AircraftFlightPlan());
        LiveTrafficKinematics.Resync(shadow, sample.ObservedAtSimSeconds, weather);
        shadow.Position = new LatLon(sample.Lat + 0.5, sample.Lon + 0.5);
        shadow.Altitude = sample.AltitudeFt + 3000;
        return shadow;
    }

    private static GroundNode StandNode(AirportGroundLayout layout) =>
        layout.Nodes.Values.First(node => (node.Type == GroundNodeType.Parking) && (node.Name == Stand));

    private static LiveTrafficSample GroundSample(LatLon position, double groundSpeedKts) =>
        new(0, position.Lat, position.Lon, 13, groundSpeedKts, 280, 0, LiveTrafficSource.Asdex, null);

    private static string ParkedJson(string callsign, string destination) =>
        $$"""
            {
              "id": "test", "name": "Test", "primaryAirportId": "SFO",
              "aircraft": [{
                "id": "ac1", "aircraftId": "{{callsign}}", "aircraftType": "B738", "airportId": "SFO",
                "startingConditions": { "type": "Parking", "parking": "{{Stand}}" },
                "flightplan": { "rules": "IFR", "departure": "KSFO", "destination": "{{destination}}", "cruiseAltitude": 35000, "route": "SUNOL" }
              }]
            }
            """;

    private static string OnRunwayJson(string callsign, string airport, string runway) =>
        $$"""
            {
              "id": "test", "name": "Test", "primaryAirportId": "SFO",
              "aircraft": [{
                "id": "ac1", "aircraftId": "{{callsign}}", "aircraftType": "B738", "airportId": "{{airport}}",
                "startingConditions": { "type": "OnRunway", "runway": "{{runway}}" },
                "flightplan": { "rules": "IFR", "departure": "K{{airport}}", "destination": "KLAX", "cruiseAltitude": 35000, "route": "SUNOL" }
              }]
            }
            """;

    private static string GroundCoordinatesJson(string callsign, LatLon position) =>
        $$"""
            {
              "id": "test", "name": "Test", "primaryAirportId": "SFO",
              "aircraft": [{
                "id": "ac1", "aircraftId": "{{callsign}}", "aircraftType": "B737", "airportId": "SFO",
                "startingConditions": { "type": "Coordinates", "coordinates": { "lat": {{position.Lat.ToString(
                "R",
                CultureInfo.InvariantCulture
            )}}, "lon": {{position.Lon.ToString("R", CultureInfo.InvariantCulture)}} } },
                "flightplan": { "rules": "IFR", "departure": "KSFO", "destination": "KLAS", "cruiseAltitude": 33000, "route": "SUNOL" }
              }]
            }
            """;

    private static string AirborneJson(string callsign, string rules, string navigationPath) =>
        $$"""
            {
              "id": "test", "name": "Test", "primaryAirportId": "SFO",
              "aircraft": [{
                "id": "ac1", "aircraftId": "{{callsign}}", "aircraftType": "A320",
                "startingConditions": {
                  "type": "Coordinates", "coordinates": { "lat": 37.6, "lon": -121.95 },
                  "altitude": 11000, "speed": 250, "heading": 90, "navigationPath": "{{navigationPath}}"
                },
                "flightplan": {
                  "rules": "{{rules}}", "departure": "KSFO", "destination": "KSAC", "cruiseAltitude": 11000, "route": "{{navigationPath}}"
                }
              }]
            }
            """;

    private static string OnFinalJson(string callsign, string airport, string runway, double distanceNm, string destination) =>
        $$"""
            {
              "id": "test", "name": "Test", "primaryAirportId": "SFO",
              "aircraft": [{
                "id": "ac1", "aircraftId": "{{callsign}}", "aircraftType": "B738", "airportId": "{{airport}}",
                "startingConditions": { "type": "OnFinal", "runway": "{{runway}}", "distanceFromRunway": {{distanceNm.ToString(
                "R",
                CultureInfo.InvariantCulture
            )}} },
                "flightplan": { "rules": "IFR", "departure": "KLAX", "destination": "{{destination}}", "cruiseAltitude": 35000, "route": "SUNOL" }
              }]
            }
            """;

    private static string OnFinalNoPlanJson(string callsign, string airport, string runway, double distanceNm) =>
        $$"""
            {
              "id": "test", "name": "Test", "primaryAirportId": "SFO",
              "aircraft": [{
                "id": "ac1", "aircraftId": "{{callsign}}", "aircraftType": "C172", "airportId": "{{airport}}",
                "startingConditions": { "type": "OnFinal", "runway": "{{runway}}", "distanceFromRunway": {{distanceNm.ToString(
                "R",
                CultureInfo.InvariantCulture
            )}} }
              }]
            }
            """;
}

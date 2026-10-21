using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// PTAC with <c>PA</c> keeps the altitude still assigned (7110.65 §4-5-7.h.11 NOTE 1, §5-6-2.d NOTE):
/// the last climb or descent clearance, unless a descend via or climb via replaced it. With nothing
/// assigned, PA is the present altitude rounded to 100 ft toward the direction of travel, never a
/// procedure target. PTAC records its altitude as assigned and cancels the earlier vertical clearance,
/// as CM and DM do. Real KOAK navdata and the real I28R approach; the aircraft is a B738 southwest of
/// the field, its vertical state staged on its fields before the PTAC.
/// </summary>
public class PtacAssignedAltitudeTests
{
    private const string Callsign = "N917YA";
    private const string Airport = "OAK";
    private const double StartLat = 37.68;
    private const double StartLon = -122.42;
    private const double StartMagneticHeading = 280;

    private readonly ITestOutputHelper _output;

    public PtacAssignedAltitudeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void PtacPa_DescendingToAssigned_KeepsTheAssignedAltitude()
    {
        if (BuildHarness(altitudeFt: 7200) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        // A step-down target above the assigned altitude: the old target-first resolution would keep 7000
        harness.Aircraft.Targets.AssignedAltitude = 5000;
        harness.Aircraft.Targets.TargetAltitude = 7000;

        SendPtacPa(harness);

        AssertAltitudes(harness, 5000);
    }

    [Fact]
    public void PtacPa_ClimbingToAssigned_KeepsTheAssignedAltitude()
    {
        if (BuildHarness(altitudeFt: 4500) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        // A target below the assigned altitude: the old target-first resolution would keep 4000
        harness.Aircraft.Targets.AssignedAltitude = 6000;
        harness.Aircraft.Targets.TargetAltitude = 4000;

        SendPtacPa(harness);

        AssertAltitudes(harness, 6000);
    }

    [Fact]
    public void PtacPa_DescendVia_UsesPresentRoundedDown()
    {
        if (BuildHarness(altitudeFt: 13437) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.Targets.AssignedAltitude = 24000;
        harness.Aircraft.Procedure.StarViaMode = true;
        harness.Aircraft.Targets.TargetAltitude = 11000;

        SendPtacPa(harness);

        AssertAltitudes(harness, 13400);
        Assert.False(harness.Aircraft.Procedure.StarViaMode);
        Assert.Null(harness.Aircraft.Procedure.StarViaFloor);

        double lowest = 13400 - FlightPhysics.AltitudeSnapFt;
        double highest = 13437 + FlightPhysics.AltitudeSnapFt;
        for (int t = 0; t < 60; t++)
        {
            harness.Engine.TickOneSecond();
            double altitude = harness.Aircraft.Altitude;
            Assert.True(
                (altitude >= lowest) && (altitude <= highest),
                $"After {t + 1} s the aircraft is at {altitude:F0} ft; PTAC PA must hold 13400, not head for 11000 or 24000"
            );
        }

        _output.WriteLine($"After 60 s: altitude {harness.Aircraft.Altitude:F0}, target {harness.Aircraft.Targets.TargetAltitude}");
        Assert.InRange(harness.Aircraft.Altitude, 13400 - FlightPhysics.AltitudeSnapFt, 13400 + FlightPhysics.AltitudeSnapFt);
    }

    [Fact]
    public void PtacPa_DescendViaExceptMaintain_UsesPresent()
    {
        if (BuildHarness(altitudeFt: 12650) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.Procedure.StarViaMode = true;
        harness.Aircraft.Procedure.StarViaFloor = 10000;
        harness.Aircraft.Targets.TargetAltitude = 10000;

        SendPtacPa(harness);

        AssertAltitudes(harness, 12600);
    }

    [Fact]
    public void PtacPa_ClimbVia_UsesPresentRoundedUp()
    {
        if (BuildHarness(altitudeFt: 4530) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.Procedure.SidViaMode = true;
        harness.Aircraft.Procedure.SidViaCeiling = 9000;
        harness.Aircraft.Targets.TargetAltitude = 9000;

        SendPtacPa(harness);

        AssertAltitudes(harness, 4600);
        Assert.False(harness.Aircraft.Procedure.SidViaMode);
        Assert.Null(harness.Aircraft.Procedure.SidViaCeiling);
    }

    [Theory]
    [InlineData(3550, 3600)]
    [InlineData(3520, 3500)]
    public void PtacPa_NothingAssigned_Level_RoundsToNearest(double presentFt, int expectedFt)
    {
        if (BuildHarness(altitudeFt: presentFt) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.Targets.AssignedAltitude = null;
        harness.Aircraft.Targets.TargetAltitude = null;

        SendPtacPa(harness);

        AssertAltitudes(harness, expectedFt);
    }

    [Fact]
    public void PtacExplicit_RecordsAssignedAndCancelsVia()
    {
        if (BuildHarness(altitudeFt: 5000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.Procedure.StarViaMode = true;
        harness.Aircraft.Procedure.StarViaFloor = 4000;
        harness.Aircraft.Targets.AssignedAltitude = 24000;
        harness.Aircraft.Targets.AltitudeFloor = 4000;
        harness.Aircraft.Targets.AltitudeCeiling = 8000;
        harness.Aircraft.Procedure.IsExpediting = true;

        CommandResult ptac = Send(harness, "PTAC PH 030 I28R");
        _output.WriteLine($"PTAC PH 030 -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);

        AssertAltitudes(harness, 3000);
        Assert.Contains("maintain 3000", ptac.Message, StringComparison.Ordinal);
        Assert.False(harness.Aircraft.Procedure.StarViaMode);
        Assert.Null(harness.Aircraft.Procedure.StarViaFloor);
        Assert.False(harness.Aircraft.Procedure.SidViaMode);
        Assert.Null(harness.Aircraft.Procedure.SidViaCeiling);
        Assert.Null(harness.Aircraft.Targets.AltitudeFloor);
        Assert.Null(harness.Aircraft.Targets.AltitudeCeiling);
        Assert.False(harness.Aircraft.Procedure.IsExpediting);
    }

    [Fact]
    public void PtacPa_DescendViaNoTarget_RoundsByVerticalSpeed()
    {
        if (BuildHarness(altitudeFt: 13460) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.Procedure.StarViaMode = true;
        harness.Aircraft.Targets.TargetAltitude = null;
        harness.Aircraft.VerticalSpeed = -1500;

        SendPtacPa(harness);

        AssertAltitudes(harness, 13400);
    }

    /// <summary>
    /// A target under the 50 ft deadband does not decide the rounding direction; the vertical speed does.
    /// The second row puts the target on the other side of the aircraft, so it would round the wrong way if it decided.
    /// </summary>
    [Theory]
    [InlineData(4960, 5000, 1500, 5000)]
    [InlineData(4960, 4920, 1500, 5000)]
    public void PtacPa_TargetInsideDeadband_RoundsByVerticalSpeed(double presentFt, double targetFt, double verticalSpeedFpm, int expectedFt)
    {
        if (BuildHarness(altitudeFt: presentFt) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.Procedure.StarViaMode = true;
        harness.Aircraft.Targets.TargetAltitude = targetFt;
        harness.Aircraft.VerticalSpeed = verticalSpeedFpm;

        SendPtacPa(harness);

        AssertAltitudes(harness, expectedFt);
    }

    /// <summary>
    /// A VFR "at or above" / "at or below" restriction is a bound, not an altitude to maintain, so PA
    /// takes the present altitude rather than the bound <c>AssignedAltitude</c> records.
    /// </summary>
    [Theory]
    [InlineData("CM A055")]
    [InlineData("CM B095")]
    public void PtacPa_AfterVfrAltitudeBound_UsesPresentAltitude(string boundCommand)
    {
        if (BuildHarness(altitudeFt: 7500) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        harness.Aircraft.FlightPlan.FlightRules = "VFR";
        SendOk(harness, boundCommand);

        SendPtacPa(harness);

        AssertAltitudes(harness, 7500);
    }

    [Fact]
    public void OnOakes3_DescendViaThenDescendMaintain_PtacPaKeepsTheDm()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        SendOk(harness, "DVIA");
        Assert.True(harness.Aircraft.Procedure.StarViaMode);
        TickSeconds(harness, 5);
        SendOk(harness, "DM 080");
        TickSeconds(harness, 5);

        SendPtacPa(harness);

        AssertAltitudes(harness, 8000);
    }

    [Fact]
    public void OnOakes3_DescendViaThenClimbMaintain_PtacPaKeepsTheCm()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        SendOk(harness, "DVIA");
        Assert.True(harness.Aircraft.Procedure.StarViaMode);
        TickSeconds(harness, 5);
        SendOk(harness, "CM 170");
        Assert.False(harness.Aircraft.Procedure.StarViaMode, "CM cancels the descend via (7110.65 §4-5-7.h.11 NOTE 1)");
        TickSeconds(harness, 5);

        SendPtacPa(harness);

        AssertAltitudes(harness, 17000);
    }

    [Fact]
    public void OnOakes3_DescendVia_PtacPaUsesPresentRoundedDown()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        SendOk(harness, "DVIA");
        TickSeconds(harness, 15);
        AircraftState aircraft = harness.Aircraft;
        double present = aircraft.Altitude;
        double viaTarget = Assert.IsType<double>(aircraft.Targets.TargetAltitude);
        _output.WriteLine($"Descending via: altitude {present:F0}, target {viaTarget:F0}, assigned {aircraft.Targets.AssignedAltitude}");
        Assert.True(viaTarget < present - 50, $"Expected the descend via to be descending; target {viaTarget:F0}, altitude {present:F0}");

        SendPtacPa(harness);

        int expected = (int)(Math.Floor(present / 100.0) * 100);
        AssertAltitudes(harness, expected);
    }

    [Fact]
    public void OnNimi5_ClimbVia_PtacPaUsesPresentRoundedUpWithoutWarning()
    {
        if (BuildHarness(altitudeFt: 4530) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        aircraft.FlightPlan.Departure = "KOAK";
        aircraft.FlightPlan.Route = "NIMI5 OAK V6 SAC";
        aircraft.Targets.TargetAltitude = 10000;

        SendOk(harness, "CVIA");
        Assert.NotNull(aircraft.Procedure.ActiveSidId);
        Assert.True(aircraft.Procedure.SidViaMode);
        aircraft.PendingWarnings.Clear();

        SendPtacPa(harness);

        AssertAltitudes(harness, 4600);
        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("without an altitude assignment", StringComparison.Ordinal));
    }

    [Fact]
    public void OnOakes3_CrossFixPendingRevert_PtacPaKeepsTheCrossingAltitude()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DM 100");
        string fix = aircraft.Targets.NavigationRoute.Select(t => t.Name).Last(name => !name.StartsWith("RW", StringComparison.OrdinalIgnoreCase));
        SendOk(harness, $"CFIX {fix} 6000");
        _output.WriteLine($"After CFIX {fix}: assigned {aircraft.Targets.AssignedAltitude}, target {aircraft.Targets.TargetAltitude}");

        SendPtacPa(harness);
        AssertAltitudes(harness, 6000);

        TickSeconds(harness, 60);
        AssertAltitudes(harness, 6000);
    }

    private void SendPtacPa(Harness harness)
    {
        CommandResult ptac = Send(harness, "PTAC PH PA I28R");
        _output.WriteLine($"PTAC PH PA -> {ptac.Message}");
        Assert.True(ptac.Success, ptac.Message);
    }

    private void SendOk(Harness harness, string command)
    {
        CommandResult result = Send(harness, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void TickSeconds(Harness harness, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            harness.Engine.TickOneSecond();
        }
    }

    private static void AssertAltitudes(Harness harness, int expectedFt)
    {
        Assert.Equal(expectedFt, harness.Aircraft.Targets.TargetAltitude);
        Assert.Equal(expectedFt, harness.Aircraft.Targets.AssignedAltitude);
    }

    private static CommandResult Send(Harness harness, string command) => harness.Engine.SendCommand(harness.Aircraft.Callsign, command);

    /// <summary>A generated jet established on Oakland's OAKES3 STAR, level at 16,000 with the STAR in its filed route.</summary>
    private Harness? BuildOakesHarness()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout(Airport);
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
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
        (AircraftState? aircraft, string? error) = AircraftGenerator.Generate(
            request,
            "KOAK",
            [],
            groundLayout: null,
            new Random(1),
            new BeaconCodePool()
        );
        Assert.Null(error);
        Assert.NotNull(aircraft);
        aircraft.Ground.Layout = layout;

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "ptac-assigned-altitude-oakes",
            ScenarioName = "PTAC assigned altitude on OAKES3",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = Airport,
        };

        string route = string.Join(" ", aircraft.Targets.NavigationRoute.Select(t => t.Name));
        _output.WriteLine($"Spawned {aircraft.Callsign} {aircraft.AircraftType} at {aircraft.Altitude:F0}, route {route}");
        return new Harness(engine, aircraft);
    }

    private Harness? BuildHarness(double altitudeFt)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout(Airport);
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        var position = new LatLon(StartLat, StartLon);
        double declination = MagneticDeclination.GetDeclination(position, MagneticDeclination.EvaluationDateUtc);
        TrueHeading trueHeading = new MagneticHeading(StartMagneticHeading).ToTrue(declination);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = trueHeading,
            TrueTrack = trueHeading,
            Altitude = altitudeFt,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            Declination = declination,
            DeclinationCachePosition = position,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = Airport,
                Destination = Airport,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(24000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Ground.Layout = layout;

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "ptac-assigned-altitude",
            ScenarioName = "PTAC assigned altitude",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = Airport,
        };

        return new Harness(engine, aircraft);
    }

    private sealed record Harness(SimulationEngine Engine, AircraftState Aircraft);
}

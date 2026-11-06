using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1362: a speed the aircraft was able to accept but that becomes unflyable as it climbs or descends is capped at
/// the type's limit (the lower of Vmo and Mmo's IAS equivalent at the altitude), recomputed every tick. The assignment
/// stands behind the cap, as it does behind 14 CFR 91.117. The A320's figures are EASA TCDS A.064 III.10: VMO 350 kt,
/// MMO 0.82, crossover near FL240 on ISA.
/// </summary>
public sealed class SpeedEnvelopeCapTests
{
    private const double A320VmoKts = 350;
    private const double A320Mmo = 0.82;
    private const double SpeedSnapKts = 2;
    private const double MachTolerance = 0.005;

    private readonly ITestOutputHelper _output;

    public SpeedEnvelopeCapTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState A320At(double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "DAL334",
            AircraftType = "A320",
            Position = new LatLon(34.771654, -114.542825),
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KPHX",
                Destination = "KLAX",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    private static void FlySeconds(AircraftState aircraft, int seconds, Action<AircraftState> eachSecond)
    {
        for (int second = 0; second < seconds; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                FlightPhysics.Update(aircraft, 0.25);
            }

            eachSecond(aircraft);
        }
    }

    private static double CrossoverAltitudeFt()
    {
        double altitude = 20_000;
        while (WindInterpolator.MachToIas(A320Mmo, altitude) > A320VmoKts)
        {
            altitude += 100;
        }

        return altitude;
    }

    [Fact]
    public void AssignedSpeed_ClimbingPastCrossover_IsCappedAtMmo()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPD 340").Success);
        Assert.True(Send(ac, "CM 350").Success);
        double crossover = CrossoverAltitudeFt();

        double worstMach = 0;
        FlySeconds(
            ac,
            900,
            a =>
            {
                if (a.Altitude > crossover)
                {
                    worstMach = Math.Max(worstMach, WindInterpolator.IasToMach(a.IndicatedAirspeed, a.Altitude));
                }
            }
        );

        Assert.InRange(ac.Altitude, 34_990, 35_010);
        Assert.True(worstMach <= A320Mmo + MachTolerance, $"climbing with SPD 340 above the crossover flew Mach {worstMach:F3}, past MMO {A320Mmo}");
        Assert.Equal(340, Assert.IsType<double>(ac.Targets.AssignedSpeed), 0.5);
        Assert.NotNull(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void MachHoldThenDescent_StaysAtOrBelowVmo()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: 260);
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DM 110").Success);

        double peakIas = 0;
        FlySeconds(ac, 900, a => peakIas = Math.Max(peakIas, a.IndicatedAirspeed));

        Assert.True(ac.Altitude < 20_000, $"premise: the descent reaches well below the crossover, ended at {ac.Altitude:F0} ft");
        Assert.True(peakIas <= A320VmoKts + SpeedSnapKts, $"MACH .78 then DM 110 flew {peakIas:F1} KIAS, past VMO {A320VmoKts}");
    }

    [Fact]
    public void Mach_ThenCfixWithSpeed_KeepsTheMachHoldUntilTheSwitch()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);

        CommandResult fix = Send(ac, "CFIX SUNOL 200 250");

        Assert.True(fix.Success, fix.Message);
        Assert.Equal(0.78, ac.Targets.TargetMach);

        FlySeconds(ac, 120, _ => { });

        // YAAT-1839: the crossing speed is planned to the fix, so the Mach is held down the long leg to SUNOL until the
        // planner hands it over to the crossing speed, and the IAS follows the Mach rather than freezing at its issue figure.
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.InRange(ac.IndicatedAirspeed, WindInterpolator.MachToIas(0.78, ac.Altitude) - 2, WindInterpolator.MachToIas(0.78, ac.Altitude) + 2);
    }

    /// <summary>
    /// YAAT-1445/YAAT-1422: the speed a CFIX carries reverts past its fix, so a Mach hold that speed replaced comes back
    /// there.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixWithSpeed_RestoresTheMachPastTheFix()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "CFIX SUNOL 350 250").Success);

        FlySeconds(ac, 120, _ => { });

        // YAAT-1839: the Mach is held until the planner hands it over to the crossing speed, near the fix
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Equal((double?)250, ac.Targets.AssignedSpeed);

        for (int second = 0; (second < 9_000) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        Assert.False(ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"), "the aircraft never sequenced SUNOL");
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    /// <summary>
    /// YAAT-1422: a CFIX at or below FL240 restores the Mach its speed replaced as the IAS the handover would give it
    /// there — the profile's descent speed, never the Mach's higher IAS equivalent at that altitude.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixSunol200At250_ResumesTheCappedIasPastTheFix()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "CFIX SUNOL 200 250").Success);

        for (int second = 0; (second < 9_000) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        _output.WriteLine($"Sequenced SUNOL at {ac.Altitude:F0} ft, assigned {ac.Targets.AssignedSpeed}");
        Assert.False(ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"), "the aircraft never sequenced SUNOL");
        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal((double?)290, ac.Targets.AssignedSpeed);

        double peakIas = ac.IndicatedAirspeed;
        FlySeconds(ac, 600, a => peakIas = Math.Max(peakIas, a.IndicatedAirspeed));

        Assert.True(peakIas <= 292, $"past the fix the aircraft flew {peakIas:F0} KIAS");
    }

    /// <summary>
    /// YAAT-1422: a Mach nobody assigned (live traffic seeds one with no explicit speed command) comes back with no
    /// assigned speed, as it had none before the CFIX speed replaced it.
    /// </summary>
    [Fact]
    public void UnassignedMach_RestoredPastCfix_SetsNoAssignedSpeed()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: 260);
        ac.Targets.TargetMach = WindInterpolator.IasToMach(ac.IndicatedAirspeed, ac.Altitude);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);

        Assert.True(Send(ac, "CFIX SUNOL 350 250").Success);

        for (int second = 0; (second < 9_000) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        _output.WriteLine($"Sequenced SUNOL at {ac.Altitude:F0} ft, Mach {ac.Targets.TargetMach}");
        Assert.False(ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"), "the aircraft never sequenced SUNOL");
        Assert.NotNull(ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void Mach_ThenCfixAltitudeOnly_KeepsTheMachHold()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);

        CommandResult fix = Send(ac, "CFIX SUNOL 200");

        Assert.True(fix.Success, fix.Message);
        Assert.Equal(0.78, ac.Targets.TargetMach);
    }

    [Fact]
    public void Mach_ThenConstrainedDctf_KeepsTheMachHoldForThePlanner()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);

        CommandResult dct = CommandDispatcher.Dispatch(
            SpeedEnvelopeRefusalTests.ConstrainedDirectToSunol(20_000, 250),
            ac,
            TestDispatch.Context(new SerializableRandom(1))
        );

        // YAAT-1839: the planner, not the direct-to, ends the Mach where the crossing speed takes over
        Assert.True(dct.Success, dct.Message);
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.False(ac.Targets.SpeedMaxHeld);
    }

    /// <summary>
    /// YAAT-1445/YAAT-1422: a constrained DCTF's speed reverts past its fix, and at or below FL240 the restored Mach
    /// resumes as the IAS the handover would give it there, as a CFIX's does.
    /// </summary>
    [Fact]
    public void Mach_ThenConstrainedDctf_RestoresTheMachPastTheFix()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);

        CommandResult dct = CommandDispatcher.Dispatch(
            SpeedEnvelopeRefusalTests.ConstrainedDirectToSunol(20_000, 250),
            ac,
            TestDispatch.Context(new SerializableRandom(1))
        );

        Assert.True(dct.Success, dct.Message);
        Assert.Equal(0.78, ac.Targets.TargetMach);

        FlySeconds(ac, 120, _ => { });

        Assert.Equal(0.78, ac.Targets.TargetMach);

        for (int second = 0; (second < 9_000) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        _output.WriteLine($"Sequenced SUNOL at {ac.Altitude:F0} ft, assigned {ac.Targets.AssignedSpeed}");
        Assert.False(ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"), "the aircraft never sequenced SUNOL");
        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal((double?)290, ac.Targets.AssignedSpeed);

        double peakIas = ac.IndicatedAirspeed;
        FlySeconds(ac, 600, a => peakIas = Math.Max(peakIas, a.IndicatedAirspeed));

        Assert.True(peakIas <= 292, $"past the fix the aircraft flew {peakIas:F0} KIAS");
    }

    /// <summary>
    /// YAAT-1422: a second crossing speed issued before the fix holding the Mach revert takes it over, so the Mach does not
    /// come back at the first fix and override the later crossing, and it comes back past the last one. The speed each
    /// crossing is flown at is the crossing-speed planner's business, which ends the Mach for the first crossing; only the
    /// later one is pinned within the 2 kt snap.
    /// </summary>
    [Fact]
    public void Mach_TwoChainedCrossings_FliesTheSecondCrossingSpeed()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        Assert.True(Send(ac, "CFIX HEC 300 250").Success);
        Assert.True(Send(ac, "CFIX PMD 280 240").Success);

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        Assert.True(route.FindIndex(fix => fix.Name == "HEC") < route.FindIndex(fix => fix.Name == "PMD"), "premise: HEC comes before PMD");
        Assert.Null(route.Single(fix => fix.Name == "HEC").RevertMach);
        Assert.Equal((double?)0.78, route.Single(fix => fix.Name == "PMD").RevertMach);

        for (int second = 0; (second < 3_600) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "HEC"); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        Assert.False(ac.Targets.NavigationRoute.Exists(fix => fix.Name == "HEC"), "the aircraft never sequenced HEC");
        _output.WriteLine($"Crossed HEC at {ac.IndicatedAirspeed:F0} KIAS, {ac.Altitude:F0} ft");

        double? crossedPmd = null;
        for (int second = 0; (second < 3_600) && (crossedPmd is null); second++)
        {
            FlySeconds(ac, 1, _ => { });
            if (!ac.Targets.NavigationRoute.Exists(fix => fix.Name == "PMD"))
            {
                crossedPmd = ac.IndicatedAirspeed;
                break;
            }

            Assert.Null(ac.Targets.TargetMach);
        }

        _output.WriteLine($"Crossed PMD at {crossedPmd:F0} KIAS, {ac.Altitude:F0} ft, Mach {ac.Targets.TargetMach}");
        Assert.NotNull(crossedPmd);
        Assert.InRange(crossedPmd.Value, 238, 242);
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1422: a crossing speed restated on the fix that already holds the Mach revert replaces the first crossing
    /// speed, not the Mach the first one replaced: past the fix the Mach still comes back, as the capped IAS below FL240.
    /// </summary>
    [Fact]
    public void Mach_CrossingSpeedRestatedOnTheSameFix_KeepsTheMachRevert()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "CFIX SUNOL 200 250").Success);
        Assert.True(Send(ac, "CFIX SUNOL 200 240").Success);

        NavigationTarget sunol = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("SUNOL", sunol.Name);
        Assert.Equal(240, sunol.SpeedRestriction?.SpeedKts);
        Assert.Equal((double?)0.78, sunol.RevertMach);

        FlyUntilSequenced(ac, "SUNOL");

        _output.WriteLine($"Sequenced SUNOL at {ac.Altitude:F0} ft, assigned {ac.Targets.AssignedSpeed}");
        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal((double?)290, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1686: a crossing speed restated outside any transmission (a queued block restored from a snapshot that predates
    /// transmission ids) on a fix whose Mach revert carries no speed stamp keeps the Mach revert: the fix is still the
    /// controller's crossing, flown at the new speed, and the Mach comes back past it.
    /// </summary>
    [Fact]
    public void Mach_CrossingRestatedWithoutATransmission_OnAnUnstampedHolder_KeepsTheMachRevert()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        DispatchContext noTransmission = TestDispatch.Context(new SerializableRandom(1));
        Assert.Equal(0, noTransmission.TransmissionId);
        Assert.True(NavigationCommandHandler.DispatchCrossFix(CrossFix("CFIX PMD 350 250"), ac, noTransmission).Success);
        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Null(pmd.SpeedTransmissionId);
        Assert.Equal((double?)0.78, pmd.RevertMach);

        // No speed targeted or assigned when the restate arrives, so the capture it would write is empty.
        ac.Targets.AssignedSpeed = null;
        ac.Targets.TargetSpeed = null;
        Assert.True(NavigationCommandHandler.DispatchCrossFix(CrossFix("CFIX PMD 350 240"), ac, noTransmission).Success);

        Assert.Equal((double?)0.78, pmd.RevertMach);
        FlyUntilSequenced(ac, "PMD");
        _output.WriteLine($"Sequenced PMD at {ac.IndicatedAirspeed:F1} KIAS, Mach {ac.Targets.TargetMach}, assigned {ac.Targets.AssignedSpeed}");
        Assert.InRange(ac.IndicatedAirspeed, 238, 244);
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    private static CrossFixCommand CrossFix(string text) =>
        Assert.IsType<CrossFixCommand>(Assert.Single(Assert.Single(CommandParser.ParseCompound(text).Value!.Blocks).Commands));

    /// <summary>
    /// YAAT-1422: a crossing speed issued on a fix before the one that holds the Mach revert leaves the revert where it is,
    /// on the route-order-last fix whose speed the controller set.
    /// </summary>
    [Fact]
    public void Mach_EarlierCrossingAfterALaterOne_LeavesTheRevertOnTheLater()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        Assert.True(Send(ac, "CFIX PMD 280 240").Success);
        Assert.True(Send(ac, "CFIX HEC 300 250").Success);

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        Assert.Equal(["HEC", "PMD"], route.Select(fix => fix.Name));
        Assert.Equal(250, route[0].SpeedRestriction?.SpeedKts);
        Assert.Equal(240, route[1].SpeedRestriction?.SpeedKts);
        Assert.Null(route[0].RevertMach);
        Assert.Equal((double?)0.78, route[1].RevertMach);
        Assert.Equal((bool?)true, route[1].RevertMachExplicit);
    }

    /// <summary>
    /// YAAT-1446/YAAT-1686: a MACH in a later transmission than a crossing speed cancels that crossing; a CFIX then issued
    /// on the earlier fix captures the Mach hold as its revert, so the aircraft crosses that fix at its speed and flies the
    /// Mach past the later one.
    /// </summary>
    [Fact]
    public void Mach_InALaterTransmissionAfterACrossing_CancelsTheCrossingAndKeepsTheMachPastBothFixes()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        Assert.True(Send(ac, "CFIX PMD 350 240").Success);
        Assert.True(Send(ac, "MACH .78").Success);

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        NavigationTarget pmd = Assert.Single(route, fix => fix.Name == "PMD");
        Assert.Null(pmd.SpeedRestriction);
        Assert.Null(pmd.SpeedTransmissionId);
        Assert.DoesNotContain(route, NavigationCommandHandler.HasSpeedRevert);

        Assert.True(Send(ac, "CFIX HEC 350 250").Success);
        Assert.Equal((double?)0.78, Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "HEC").RevertMach);

        FlyUntilSequenced(ac, "HEC");
        _output.WriteLine($"Crossed HEC at {ac.IndicatedAirspeed:F0} KIAS, Mach {ac.Targets.TargetMach}");
        Assert.Equal(0.78, ac.Targets.TargetMach);

        FlyUntilSequenced(ac, "PMD");
        _output.WriteLine($"Crossed PMD at {ac.IndicatedAirspeed:F0} KIAS, Mach {ac.Targets.TargetMach}");
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1446/YAAT-1686: a MACH in the crossing's own transmission leaves the crossing standing and makes the Mach its
    /// revert; a later CFIX on the earlier fix captures what the aircraft held then (the 240, with no speed to go back to),
    /// and past the later fix the Mach comes back. The captured shape this test reads (an assigned speed with no
    /// <c>RevertSpeed</c>) is the one YAAT-1918 leaves unapplied, so it will move when that defect is fixed.
    /// </summary>
    [Fact]
    public void Mach_InTheCrossingsOwnTransmission_LeavesTheCrossingAndRevertsTheMachPastIt()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        Assert.True(Send(ac, "CFIX PMD 350 240, MACH .78").Success);
        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal((double?)240, ac.Targets.AssignedSpeed);
        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Equal(240, pmd.SpeedRestriction?.SpeedKts);
        Assert.Equal((double?)0.78, pmd.RevertMach);

        Assert.True(Send(ac, "CFIX HEC 350 250").Success);
        NavigationTarget hec = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "HEC");
        Assert.Null(hec.RevertMach);
        Assert.Equal((double?)240, hec.RevertAssignedSpeed);
        Assert.Null(hec.RevertSpeed);
        Assert.Equal((double?)0.78, pmd.RevertMach);

        FlyUntilSequenced(ac, "HEC");

        FlyUntilSequenced(ac, "PMD");
        _output.WriteLine($"Crossed PMD at {ac.IndicatedAirspeed:F0} KIAS, Mach {ac.Targets.TargetMach}, assigned {ac.Targets.AssignedSpeed}");
        Assert.InRange(ac.IndicatedAirspeed, 235, 245);
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1422: a constrained DCTF whose speed is on a fix with no crossing altitude puts the Mach revert on that speed
    /// fix, not on the altitude-constrained one, and the Mach comes back past it.
    /// </summary>
    [Fact]
    public void Mach_ConstrainedDctfSpeedOnAFixWithoutAltitude_RestoresTheMachPastIt()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);

        var direct = new ConstrainedForceDirectToCommand(
            [RouteFix("HEC"), RouteFix("PMD")],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(30_000, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [1] = 240 },
            null
        );
        CommandResult dct = CommandDispatcher.Dispatch(direct, ac, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(dct.Success, dct.Message);

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        Assert.Equal(["HEC", "PMD"], route.Select(fix => fix.Name));
        Assert.NotNull(route[0].AltitudeRestriction);
        Assert.Null(route[0].SpeedRestriction);
        Assert.Null(route[1].AltitudeRestriction);
        Assert.Equal(240, route[1].SpeedRestriction?.SpeedKts);
        Assert.Null(route[0].RevertMach);
        Assert.Equal((double?)0.78, route[1].RevertMach);

        FlyUntilSequenced(ac, "PMD");

        _output.WriteLine($"Sequenced PMD at {ac.Altitude:F0} ft, Mach {ac.Targets.TargetMach}");
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1422 (D2): the ruling holds with no altitude constraint at all: a constrained DCTF whose only constraint is a
    /// speed still puts the Mach revert on that speed fix, and the Mach comes back past it.
    /// </summary>
    [Fact]
    public void Mach_ConstrainedDctfSpeedOnAFixWithoutAltitude_RestoresTheMachPastIt_NoAltitudeConstraint()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);

        var direct = new ConstrainedForceDirectToCommand([RouteFix("HEC"), RouteFix("PMD")], [], new Dictionary<int, int> { [1] = 240 }, null);
        CommandResult dct = CommandDispatcher.Dispatch(direct, ac, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(dct.Success, dct.Message);

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        Assert.Equal(["HEC", "PMD"], route.Select(fix => fix.Name));
        Assert.Null(route[0].AltitudeRestriction);
        Assert.Null(route[0].SpeedRestriction);
        Assert.Null(route[1].AltitudeRestriction);
        Assert.Equal(240, route[1].SpeedRestriction?.SpeedKts);
        Assert.Null(route[0].RevertMach);
        Assert.Equal((double?)0.78, route[1].RevertMach);
        Assert.False(NavigationCommandHandler.HasPlainRevert(route[1]));

        FlyUntilSequenced(ac, "PMD");
        _output.WriteLine($"Sequenced PMD at {ac.IndicatedAirspeed:F0} KIAS, Mach {ac.Targets.TargetMach}");
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1422: a Mach revert pending from an earlier CFIX moves to the last fix whose speed a later constrained DCTF
    /// sets, so the direct-to's crossing speeds are flown first.
    /// </summary>
    [Fact]
    public void Mach_PendingRevertThenConstrainedDctf_MovesTheRevertToTheDctfSpeedFix()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        Assert.True(Send(ac, "CFIX HEC 300 250").Success);
        Assert.Equal((double?)0.78, ac.Targets.NavigationRoute.Single(fix => fix.Name == "HEC").RevertMach);

        var direct = new ConstrainedForceDirectToCommand(
            [RouteFix("HEC"), RouteFix("PMD")],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(30_000, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [0] = 250, [1] = 240 },
            null
        );
        CommandResult dct = CommandDispatcher.Dispatch(direct, ac, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(dct.Success, dct.Message);

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        Assert.Equal(["HEC", "PMD"], route.Select(fix => fix.Name));
        Assert.NotNull(route[0].AltitudeRestriction);
        Assert.Equal(250, route[0].SpeedRestriction?.SpeedKts);
        Assert.Null(route[1].AltitudeRestriction);
        Assert.Equal(240, route[1].SpeedRestriction?.SpeedKts);
        Assert.Null(route[0].RevertMach);
        Assert.Null(route[0].RevertSpeed);
        Assert.Null(route[0].RevertAssignedSpeed);
        Assert.Equal((double?)0.78, route[1].RevertMach);
        Assert.Equal((bool?)true, route[1].RevertMachExplicit);
    }

    /// <summary>
    /// YAAT-1422: a Mach nobody assigned, restored past a CFIX below FL240 as the capped IAS, becomes a target speed only:
    /// it assigns nothing, as the Mach assigned nothing before the CFIX speed replaced it.
    /// </summary>
    [Fact]
    public void UnassignedMach_RestoredBelowFl240_TargetsTheIasWithNoAssignment()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        ac.Targets.TargetMach = 0.78;
        Assert.False(ac.Targets.HasExplicitSpeedCommand);

        Assert.True(Send(ac, "CFIX SUNOL 200 250").Success);
        NavigationTarget sunol = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal((double?)0.78, sunol.RevertMach);
        Assert.Equal((bool?)false, sunol.RevertMachExplicit);

        // Tick by tick, so the restore is read on the tick that sequences SUNOL: past it no ATC speed is in force, and the
        // aircraft's own speed schedule may retarget it later.
        for (int tick = 0; (tick < 36_000) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"); tick++)
        {
            FlightPhysics.Update(ac, 0.25);
        }

        _output.WriteLine(
            $"SUNOL at {ac.Altitude:F0} ft, {ac.IndicatedAirspeed:F1} kt, target {ac.Targets.TargetSpeed}, assigned {ac.Targets.AssignedSpeed}"
        );
        Assert.False(ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"), "the aircraft never sequenced SUNOL");
        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal((double?)290, ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    /// <summary>A route fix named <paramref name="name"/>, at its position in the real navdata.</summary>
    private static ResolvedFix RouteFix(string name)
    {
        CrossFixCommand cfix = Assert.IsType<CrossFixCommand>(CommandParser.Parse($"CFIX {name} 200").Value);
        return new ResolvedFix(name, cfix.FixLat, cfix.FixLon);
    }

    /// <summary>Flies <paramref name="aircraft"/> a second at a time until it sequences <paramref name="fixName"/>.</summary>
    private static void FlyUntilSequenced(AircraftState aircraft, string fixName)
    {
        for (int second = 0; (second < 9_000) && aircraft.Targets.NavigationRoute.Exists(fix => fix.Name == fixName); second++)
        {
            FlySeconds(aircraft, 1, _ => { });
        }

        Assert.False(aircraft.Targets.NavigationRoute.Exists(fix => fix.Name == fixName), $"the aircraft never sequenced {fixName}");
    }

    /// <summary>
    /// YAAT-1839: a crossing speed below a Mach hold's IAS keeps the Mach until the slowdown point for the crossing
    /// (7110.65 5-7-1.g; AIM 4-4-12.c): the IAS follows the Mach until then, not the figure it had when the CFIX was issued,
    /// and from there the aircraft flies the crossing IAS, then holds the Mach again past the fix.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixSpeed_FarAhead_HoldsTheMachUntilTheWindow_ThenCrossesAtTheSpeed()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "CFIX SUNOL 350 240").Success);
        Assert.Equal(0.78, ac.Targets.TargetMach);

        FlySeconds(ac, 120, _ => { });

        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Equal((double?)240, ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, WindInterpolator.MachToIas(0.78, ac.Altitude) - 2, WindInterpolator.MachToIas(0.78, ac.Altitude) + 2);

        for (int second = 0; (second < 9_000) && (ac.Targets.TargetMach is not null); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        double sunolNm = GeoMath.DistanceNm(ac.Position, ac.Targets.NavigationRoute.Single(fix => fix.Name == "SUNOL").Position);
        _output.WriteLine($"Mach ended {sunolNm:F1} nm before SUNOL at {ac.IndicatedAirspeed:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal((double?)240, ac.Targets.TargetSpeed);
        Assert.Equal((double?)240, ac.Targets.AssignedSpeed);
        Assert.True(sunolNm < 20, $"the Mach ended {sunolNm:F1} nm before SUNOL, before the slowdown point");

        double? crossed = null;
        for (int second = 0; (second < 3_600) && (crossed is null); second++)
        {
            FlySeconds(ac, 1, _ => { });
            if (!ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"))
            {
                crossed = ac.IndicatedAirspeed;
            }
        }

        _output.WriteLine($"Crossed SUNOL at {crossed:F0} KIAS");
        Assert.NotNull(crossed);
        Assert.InRange(crossed.Value, 230, 250);
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1839: a crossing speed above a Mach hold's IAS keeps the Mach until its IAS equivalent reaches the crossing
    /// speed on the descent, then flies the crossing speed, never running past it.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixSpeed_MachIasReachesTheCrossing_EndsTheMachThere()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DM 280").Success);
        Assert.True(Send(ac, "CFIX SUNOL 280 290").Success);
        Assert.True(WindInterpolator.MachToIas(0.78, 35_000) < 288, "premise: the crossing is above the Mach's IAS at FL350");
        Assert.Equal(0.78, ac.Targets.TargetMach);

        double peakIas = 0;
        for (int second = 0; (second < 9_000) && (ac.Targets.TargetMach is not null); second++)
        {
            FlySeconds(ac, 1, a => peakIas = Math.Max(peakIas, a.IndicatedAirspeed));
        }

        _output.WriteLine(
            $"Mach ended at {ac.Altitude:F0} ft, {ac.IndicatedAirspeed:F0} KIAS, Mach IAS {WindInterpolator.MachToIas(0.78, ac.Altitude):F0}"
        );
        Assert.Null(ac.Targets.TargetMach);
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.Name == "SUNOL");
        Assert.True(WindInterpolator.MachToIas(0.78, ac.Altitude) >= 288 - SpeedSnapKts, "the Mach ended before its IAS reached the crossing");
        // The target speed is cleared once the IAS reaches it.
        Assert.Equal(290, ac.Targets.TargetSpeed ?? ac.IndicatedAirspeed, SpeedSnapKts);

        FlyUntilSequenced(ac, "SUNOL");
        Assert.True(peakIas <= 300, $"the aircraft flew {peakIas:F0} KIAS on the way to a 290 crossing");
    }

    /// <summary>
    /// YAAT-1839: a Mach carried down a descent below FL240 before the crossing's slowdown point is handed over to an IAS
    /// there at the latest (7110.65 5-7-1.g), and the crossing speed stays the assigned speed until the fix.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixSpeed_DescentThroughFl240BeforeTheWindow_FliesTheIasAtFl240()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DM 110").Success);
        Assert.True(Send(ac, "CFIX SUNOL 110 250").Success);
        Assert.Equal(0.78, ac.Targets.TargetMach);

        for (int second = 0; (second < 9_000) && (ac.Targets.TargetMach is not null); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        _output.WriteLine($"Mach ended at {ac.Altitude:F0} ft, target {ac.Targets.TargetSpeed}, assigned {ac.Targets.AssignedSpeed}");
        Assert.Null(ac.Targets.TargetMach);
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.Name == "SUNOL");
        Assert.True(ac.Altitude >= 24_000 - 500, $"the Mach was carried to {ac.Altitude:F0} ft");
        // The target speed is cleared once the IAS reaches it.
        double handedOver = ac.Targets.TargetSpeed ?? ac.IndicatedAirspeed;
        double handOverKts = Math.Min(A320VmoKts - 10, WindInterpolator.MachToIas(0.78, ac.Altitude));
        Assert.InRange(handedOver, handOverKts - 2, handOverKts + 2);
        Assert.True(handedOver > 250, $"handed over to {handedOver:F0} KIAS, the crossing speed before its slowdown");
        Assert.Equal((double?)250, ac.Targets.AssignedSpeed);

        double? crossed = null;
        double? crossingTargetedNm = null;
        for (int second = 0; (second < 9_000) && (crossed is null); second++)
        {
            FlySeconds(ac, 1, _ => { });
            if (ac.Targets.NavigationRoute.Find(fix => fix.Name == "SUNOL") is not { } sunol)
            {
                crossed = ac.IndicatedAirspeed;
                continue;
            }

            Assert.Equal((double?)250, ac.Targets.AssignedSpeed);
            if ((crossingTargetedNm is null) && (ac.Targets.TargetSpeed == 250))
            {
                crossingTargetedNm = GeoMath.DistanceNm(ac.Position, sunol.Position);
            }
        }

        _output.WriteLine($"Targeted 250 {crossingTargetedNm:F1} nm before SUNOL; crossed it at {crossed:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.NotNull(crossed);
        Assert.InRange(crossed.Value, 240, 260);
        Assert.NotNull(crossingTargetedNm);
        Assert.True(crossingTargetedNm < 20, $"the 250 was targeted {crossingTargetedNm:F1} nm before SUNOL, before its slowdown point");
    }

    /// <summary>
    /// YAAT-1839: a crossing speed above a level Mach hold's IAS is accelerated to in time to cross at it: the Mach is
    /// held until the acceleration point, never all the way to the fix at its own lower IAS.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixSpeedAboveTheMachIas_Level_AcceleratesInTimeToCrossAtIt()
    {
        AircraftState ac = A320At(altitude: 28_000, ias: WindInterpolator.MachToIas(0.72, 28_000));
        Assert.True(Send(ac, "MACH .72").Success);
        Assert.True(Send(ac, "CFIX SUNOL 280 320").Success);
        Assert.True(WindInterpolator.MachToIas(0.72, 28_000) < 310, "premise: the crossing is above the Mach's IAS");

        double? crossed = null;
        for (int second = 0; (second < 9_000) && (crossed is null); second++)
        {
            FlySeconds(ac, 1, _ => { });
            if (!ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"))
            {
                crossed = ac.IndicatedAirspeed;
            }
        }

        _output.WriteLine($"Crossed SUNOL at {crossed:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.NotNull(crossed);
        Assert.InRange(crossed.Value, 310, 330);
    }

    /// <summary>
    /// YAAT-1839: a Mach held level below FL240 with a crossing speed far ahead is handed over to its own IAS there
    /// (7110.65 5-7-1.g), not slowed to the crossing at once: the crossing speed waits for its slowdown point.
    /// </summary>
    [Fact]
    public void Mach_LevelBelowFl240_ThenCfixSpeedFarAhead_FliesTheMachIasUntilTheSlowdown()
    {
        AircraftState ac = A320At(altitude: 23_000, ias: WindInterpolator.MachToIas(0.70, 23_000));
        Assert.True(Send(ac, "MACH .70, CFIX SUNOL 230 250").Success);

        FlySeconds(ac, 5, _ => { });

        // The target speed is cleared once the IAS reaches it.
        double target = ac.Targets.TargetSpeed ?? ac.IndicatedAirspeed;
        double handOverKts = Math.Round(Math.Min(A320VmoKts - 10, WindInterpolator.MachToIas(0.70, ac.Altitude)));
        _output.WriteLine($"5 s later: target {ac.Targets.TargetSpeed}, {ac.IndicatedAirspeed:F0} KIAS, hand-over {handOverKts:F0}");
        Assert.Null(ac.Targets.TargetMach);
        Assert.InRange(target, handOverKts - 2, handOverKts + 2);
        Assert.True(target > 260, $"targeted {target:F0} KIAS 5 s after the crossing was issued far ahead");

        double? crossed = null;
        for (int second = 0; (second < 9_000) && (crossed is null); second++)
        {
            FlySeconds(ac, 1, _ => { });
            if (!ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"))
            {
                crossed = ac.IndicatedAirspeed;
            }
        }

        _output.WriteLine($"Crossed SUNOL at {crossed:F0} KIAS");
        Assert.NotNull(crossed);
        Assert.InRange(crossed.Value, 240, 260);
    }

    /// <summary>
    /// YAAT-1839: a hold entered with a Mach still held for a crossing ends the Mach, so the aircraft flies the holding
    /// speed rather than the Mach's IAS around the pattern.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixSpeed_ThenHold_FliesTheHoldingSpeed()
    {
        AircraftState ac = A320At(altitude: 31_000, ias: WindInterpolator.MachToIas(0.80, 31_000));
        ResolvedFix sunol = RouteFix("SUNOL");
        ac.Position = GeoMath.ProjectPoint(new LatLon(sunol.Lat, sunol.Lon), new TrueHeading(90), 5);
        ac.Phases = new PhaseList();
        Assert.True(Send(ac, "MACH .80").Success);
        Assert.True(Send(ac, "CFIX SUNOL 310 250").Success);
        Assert.True(Send(ac, "HOLD SUNOL 090 R").Success);

        FlyWithPhases(ac, 180, () => false);

        double holding = AircraftPerformance.HoldingSpeed("A320", ac.Altitude);
        _output.WriteLine($"in the hold: {ac.IndicatedAirspeed:F0} KIAS, Mach {ac.Targets.TargetMach}, holding {holding:F0}");
        Assert.True(ac.IndicatedAirspeed <= holding + 5, $"{ac.IndicatedAirspeed:F0} KIAS in the hold, holding speed {holding:F0}");
    }

    /// <summary>
    /// YAAT-1839: a hold issued far from its fix under a Mach hold keeps the Mach all the way to the fix, and there the
    /// aircraft slows to the holding speed for the pattern. DM 250 keeps the descent above FL240, so the descent
    /// handover does not end the Mach first.
    /// </summary>
    [Fact]
    public void Mach_ThenHoldFarAhead_HoldsTheMachUntilTheFix()
    {
        AircraftState ac = HoldingAircraftEastOfSunol(120);
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DM 250").Success);
        Assert.True(Send(ac, "HOLD SUNOL 090 R").Success);

        bool atFix = FlyWithPhases(
            ac,
            3_600,
            () =>
            {
                if (!ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"))
                {
                    return true;
                }

                Assert.Equal(0.78, ac.Targets.TargetMach);
                return false;
            }
        );
        _output.WriteLine($"at SUNOL: {ac.IndicatedAirspeed:F0} KIAS, {ac.Altitude:F0} ft, Mach {ac.Targets.TargetMach}");
        Assert.True(atFix, "the hold never reached SUNOL");

        AssertFliesTheHoldingSpeedInThePattern(ac);
    }

    /// <summary>YAAT-1839: a MACH given after the HOLD, before the fix, also ends at the fix for the holding speed.</summary>
    [Fact]
    public void HoldFarAhead_ThenMach_FliesTheHoldingSpeedInThePattern()
    {
        AircraftState ac = HoldingAircraftEastOfSunol(120);
        Assert.True(Send(ac, "DM 250").Success);
        Assert.True(Send(ac, "HOLD SUNOL 090 R").Success);
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.Equal(0.78, ac.Targets.TargetMach);

        bool atFix = FlyWithPhases(ac, 3_600, () => !ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"));
        Assert.True(atFix, "the hold never reached SUNOL");

        AssertFliesTheHoldingSpeedInThePattern(ac);
    }

    /// <summary>An A320 at FL350, <paramref name="distanceNm"/> east of SUNOL and west-bound toward it, with phases.</summary>
    private static AircraftState HoldingAircraftEastOfSunol(double distanceNm)
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        ResolvedFix sunol = RouteFix("SUNOL");
        ac.Position = GeoMath.ProjectPoint(new LatLon(sunol.Lat, sunol.Lon), new TrueHeading(90), distanceNm);
        ac.Phases = new PhaseList();
        return ac;
    }

    private void AssertFliesTheHoldingSpeedInThePattern(AircraftState ac)
    {
        FlyWithPhases(ac, 240, () => false);
        double holding = AircraftPerformance.HoldingSpeed("A320", ac.Altitude);
        _output.WriteLine($"in the hold: {ac.IndicatedAirspeed:F0} KIAS, {ac.Altitude:F0} ft, Mach {ac.Targets.TargetMach}, holding {holding:F0}");
        Assert.Null(ac.Targets.TargetMach);
        Assert.InRange(ac.IndicatedAirspeed, holding - 5, holding + 5);
    }

    /// <summary>
    /// Ticks the phases and the physics for up to <paramref name="seconds"/>, so a hold reaches its fix and flies its
    /// pattern; stops early, returning true, once <paramref name="done"/> (checked each second) says so.
    /// </summary>
    private static bool FlyWithPhases(AircraftState ac, int seconds, Func<bool> done)
    {
        for (int second = 0; second < seconds; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                var ctx = new PhaseContext
                {
                    Aircraft = ac,
                    Targets = ac.Targets,
                    Category = AircraftCategorization.Categorize(ac.AircraftType),
                    DeltaSeconds = 0.25,
                    Runway = ac.Phases?.AssignedRunway,
                    ActiveRunways = ActiveRunways.Empty,
                    Logger = NullLogger.Instance,
                };
                PhaseRunner.Tick(ac, ctx);
                FlightPhysics.Update(ac, 0.25);
            }

            if (done())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// YAAT-1839: a Mach held level below FL240 with a crossing speed ahead flies the crossing's IAS and keeps the crossing
    /// assigned, rather than the handover's IAS overwriting it.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixSpeed_LevelBelowFl240_KeepsTheCrossingAssigned()
    {
        AircraftState ac = A320At(altitude: 23_000, ias: WindInterpolator.MachToIas(0.70, 23_000));
        Assert.True(Send(ac, "MACH .70, CFIX SUNOL 230 250").Success);

        FlySeconds(ac, 5, _ => { });

        // The target speed is cleared once the IAS reaches it.
        double target = ac.Targets.TargetSpeed ?? ac.IndicatedAirspeed;
        double handOverKts = Math.Round(Math.Min(A320VmoKts - 10, WindInterpolator.MachToIas(0.70, ac.Altitude)));
        Assert.Null(ac.Targets.TargetMach);
        Assert.InRange(target, handOverKts - 2, handOverKts + 2);
        Assert.Equal((double?)250, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1839: a constrained DCTF after a CFIX under a Mach hold leaves one Mach revert, past the DCTF's last crossing
    /// speed: the Mach still held after the CFIX is the one that CFIX already captured.
    /// </summary>
    [Fact]
    public void Mach_ThenCfixThenDctf_KeepsOneMachRevert()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        Assert.True(Send(ac, "CFIX HEC 350 250").Success);
        Assert.Equal(0.78, ac.Targets.TargetMach);

        var direct = new ConstrainedForceDirectToCommand(
            [RouteFix("HEC"), RouteFix("PMD")],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(35_000, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [1] = 240 },
            null
        );
        CommandResult dct = CommandDispatcher.Dispatch(direct, ac, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(dct.Success, dct.Message);

        NavigationTarget holder = Assert.Single(ac.Targets.NavigationRoute, fix => fix.RevertMach is not null);
        Assert.Equal("PMD", holder.Name);
        Assert.Equal((double?)0.78, holder.RevertMach);
        Assert.Equal((bool?)true, holder.RevertMachExplicit);

        FlyUntilSequenced(ac, "PMD");
        Assert.Equal(0.78, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// YAAT-1839: a Mach nobody assigned (live traffic) is held until the crossing's slowdown point too, and past the fix
    /// comes back with no assigned speed.
    /// </summary>
    [Fact]
    public void UnassignedMach_ThenCfixSpeed_PlansAndKeepsNoAssignedSpeedPastTheFix()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        ac.Targets.TargetMach = 0.78;
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.True(Send(ac, "CFIX SUNOL 350 240").Success);
        Assert.Equal(0.78, ac.Targets.TargetMach);

        for (int second = 0; (second < 9_000) && (ac.Targets.TargetMach is not null); second++)
        {
            FlySeconds(ac, 1, _ => { });
        }

        Assert.Null(ac.Targets.TargetMach);
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.Name == "SUNOL");
        Assert.Equal((double?)240, ac.Targets.TargetSpeed);

        double? crossed = null;
        for (int second = 0; (second < 3_600) && (crossed is null); second++)
        {
            FlySeconds(ac, 1, _ => { });
            if (!ac.Targets.NavigationRoute.Exists(fix => fix.Name == "SUNOL"))
            {
                crossed = ac.IndicatedAirspeed;
            }
        }

        Assert.NotNull(crossed);
        Assert.InRange(crossed.Value, 230, 250);
        Assert.NotNull(ac.Targets.TargetMach);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Mach_ThenWarp_ClearsTheMachHold()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000));
        Assert.True(Send(ac, "MACH .78").Success);

        CommandResult warp = Send(ac, "WARP SUNOL 270 200 280");

        Assert.True(warp.Success, warp.Message);
        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal(280, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void ChainedSpeedHeldAtCap_Completes()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPD 340, CM 350; SPD 260").Success);

        double worstMach = 0;
        int firedAt = -1;
        int second = 0;
        FlySeconds(
            ac,
            900,
            a =>
            {
                second++;
                worstMach = Math.Max(worstMach, WindInterpolator.IasToMach(a.IndicatedAirspeed, a.Altitude));
                if ((firedAt < 0) && (a.Targets.AssignedSpeed is 260))
                {
                    firedAt = second;
                }
            }
        );

        Assert.True(worstMach <= A320Mmo + MachTolerance, $"SPD 340 flew Mach {worstMach:F3} before the chain, past MMO {A320Mmo}");
        Assert.True(firedAt > 0, $"the chained SPD 260 never fired: SPD 340 held at the cap at FL{ac.Altitude / 100:F0} did not complete");
        Assert.Equal(260, ac.IndicatedAirspeed, 1.0);
    }

    [Fact]
    public void LevelCruiseAboveTheProfileCruiseAltitude_IsCappedAtMmo()
    {
        double levelCruiseIas = AircraftPerformance.DefaultSpeed("A320", AircraftCategory.Jet, 41_000, targetAltitude: null);
        AircraftState ac = A320At(altitude: 41_000, ias: levelCruiseIas);

        double worstMach = 0;
        FlySeconds(ac, 120, a => worstMach = Math.Max(worstMach, WindInterpolator.IasToMach(a.IndicatedAirspeed, a.Altitude)));

        Assert.True(
            WindInterpolator.IasToMach(ac.IndicatedAirspeed, ac.Altitude) <= A320Mmo + MachTolerance,
            $"level at FL410 the A320 settled at Mach {WindInterpolator.IasToMach(ac.IndicatedAirspeed, ac.Altitude):F3}, past MMO {A320Mmo}"
        );
        Assert.True(levelCruiseIas > WindInterpolator.MachToIas(A320Mmo, 41_000), "premise: the level-cruise IAS is past MMO at FL410");
        Assert.True(worstMach > 0, "premise: the aircraft flew");
    }

    /// <summary>
    /// Every speed the profile's schedule flies is within the type's cap: the climb and descent schedules at every
    /// 100 ft up to the ceiling and at each altitude the schedule changes band at (10,000, FL150, FL240, the cruise
    /// altitude and the ceiling), and level cruise up to the profile's cruise altitude. The cap's own floor samples a
    /// coarser grid, so a peak between its points is caught here.
    /// </summary>
    [Fact]
    public void ProfileSchedule_NeverAboveTheCap()
    {
        string profilesPath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfiles.json");
        List<string> offenders = [];
        foreach (string type in Yaat.Sim.Data.AircraftProfileDatabase.LoadFromFile(profilesPath).Keys)
        {
            if ((Yaat.Sim.Data.AircraftProfileDatabase.Get(type) is not { } p) || (AircraftPerformance.MaxOperatingIasKts(type, 0) is null))
            {
                continue;
            }

            AircraftCategory cat = AircraftCategorization.Categorize(type);
            SortedSet<double> altitudes = [10_000, 15_000, 24_000, p.CruiseAltitude, p.Ceiling];
            for (double grid = 0; grid <= p.Ceiling; grid += 100)
            {
                altitudes.Add(grid);
            }

            foreach (double alt in altitudes.Where(a => a <= p.Ceiling))
            {
                double cap = AircraftPerformance.MaxOperatingIasKts(type, alt)!.Value + 0.5;
                List<double> flown = [AircraftPerformance.ClimbSpeed(type, cat, alt), AircraftPerformance.DescentSpeed(type, cat, alt)];
                if (alt <= p.CruiseAltitude)
                {
                    flown.Add(AircraftPerformance.DefaultSpeed(type, cat, alt, targetAltitude: null));
                }

                if (flown.Max() > cap)
                {
                    offenders.Add($"{type} at {alt:F0} ft flies {flown.Max():F1} KIAS, cap {cap - 0.5:F1}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(30)) + $"\n({offenders.Count} in all)");
    }

    // ---- An assigned speed survives a cap (YAAT-1417) ------------------------------------

    /// <summary>
    /// A speed assignment stands behind the type's Mmo through a climb: the pilot flies the cap and resumes the
    /// assignment on the way back down without ATC saying anything again (7110.65 5-7-2.a NOTE 1, 5-7-4; AIM
    /// 4-4-12.f.1, 4-4-12.i).
    /// </summary>
    [Fact]
    public void AssignedSpeed_HeldAtTheMachCap_ResumesOnTheWayDown()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPD 340").Success);
        Assert.True(Send(ac, "CM 350").Success);

        FlySeconds(ac, 900, _ => { });
        Assert.InRange(ac.Altitude, 34_990, 35_010);
        double heldIas = WindInterpolator.MachToIas(A320Mmo, ac.Altitude);
        Assert.InRange(ac.IndicatedAirspeed, heldIas - SpeedSnapKts, heldIas + SpeedSnapKts);
        Assert.Equal(340, Assert.IsType<double>(ac.Targets.AssignedSpeed), 0.5);
        Assert.Equal(340, Assert.IsType<double>(ac.Targets.TargetSpeed), 0.5);

        Assert.True(Send(ac, "DM 200").Success);
        FlySeconds(ac, 900, _ => { });

        Assert.InRange(ac.Altitude, 19_990, 20_010);
        Assert.Equal(340, ac.IndicatedAirspeed, 1.0);
    }

    /// <summary>
    /// The same behind 14 CFR 91.117's 250 kt below 10,000 ft: the aircraft holds 250 down low and takes its assigned
    /// 280 back up above 10,000.
    /// </summary>
    [Fact]
    public void AssignedSpeed_HeldAtThe10kCap_ResumesAbove10000()
    {
        AircraftState ac = A320At(altitude: 12_000, ias: 280);
        Assert.True(Send(ac, "SPD 280").Success);
        Assert.True(Send(ac, "DM 080").Success);

        FlySeconds(ac, 300, _ => { });
        Assert.InRange(ac.Altitude, 7_990, 8_010);
        Assert.InRange(ac.IndicatedAirspeed, 248, 252);
        Assert.Equal(280, Assert.IsType<double>(ac.Targets.AssignedSpeed), 0.5);

        Assert.True(Send(ac, "CM 150").Success);
        FlySeconds(ac, 400, _ => { });

        Assert.InRange(ac.Altitude, 14_990, 15_010);
        Assert.Equal(280, ac.IndicatedAirspeed, SpeedSnapKts);
    }

    /// <summary>
    /// "280 or less" is a bound, not a speed to fly: after the cap it hits below 10,000 ft, the aircraft is not dragged
    /// back up to 280 on the climb.
    /// </summary>
    [Fact]
    public void SpeedCeiling_IsNotResumedAsAnAssignment()
    {
        AircraftState ac = A320At(altitude: 12_000, ias: 280);
        Assert.True(Send(ac, "SPD 280-").Success);
        Assert.True(Send(ac, "DM 080").Success);

        FlySeconds(ac, 300, _ => { });
        Assert.InRange(ac.Altitude, 7_990, 8_010);
        Assert.InRange(ac.IndicatedAirspeed, 248, 252);

        Assert.True(Send(ac, "CM 150").Success);
        FlySeconds(ac, 400, _ => { });

        Assert.InRange(ac.Altitude, 14_990, 15_010);
        Assert.True(ac.IndicatedAirspeed < 270, $"'280 or less' was flown back as a speed to reach: {ac.IndicatedAirspeed:F0} KIAS");
    }

    /// <summary>
    /// "280 or greater" is a floor, and the floor is what lifts the aircraft back to 280 above 10,000 ft — not a speed
    /// resumed as an assignment.
    /// </summary>
    [Fact]
    public void SpeedFloor_LiftsBackTo280Above10000()
    {
        AircraftState ac = A320At(altitude: 12_000, ias: 280);
        Assert.True(Send(ac, "SPD 280+").Success);
        Assert.True(Send(ac, "DM 080").Success);

        FlySeconds(ac, 300, _ => { });
        Assert.InRange(ac.Altitude, 7_990, 8_010);
        Assert.InRange(ac.IndicatedAirspeed, 248, 252);
        Assert.Null(ac.Targets.TargetSpeed); // the bound is not resumed as a standing speed to fly

        Assert.True(Send(ac, "CM 150").Success);
        FlySeconds(ac, 400, _ => { });

        Assert.InRange(ac.Altitude, 14_990, 15_010);
        Assert.True(ac.IndicatedAirspeed >= 278, $"'280 or greater' left the aircraft at {ac.IndicatedAirspeed:F0} KIAS");
    }

    /// <summary>
    /// A bound cleared by the phase that ends it takes its assignment with it: after the go-around, "280 or less" is no
    /// longer a speed to reach, so a climb through 10,000 ft follows the type's own climb schedule instead of settling on
    /// an exact 280 nothing asked for.
    /// </summary>
    [Fact]
    public void GoAround_EndsABoundAndItsAssignmentTogether()
    {
        AircraftState ac = A320At(altitude: 12_000, ias: 280);
        Assert.True(Send(ac, "SPD 280-").Success);
        Assert.Equal(280, Assert.IsType<double>(ac.Targets.AssignedSpeed));

        new GoAroundPhase { Status = PhaseStatus.Active }.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));

        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);

        Assert.True(Send(ac, "DM 080").Success);
        FlySeconds(ac, 300, _ => { });
        Assert.InRange(ac.Altitude, 7_990, 8_010);

        Assert.True(Send(ac, "CM 250").Success);
        FlySeconds(ac, 700, _ => { });

        Assert.InRange(ac.Altitude, 24_990, 25_010);
        Assert.True(ac.IndicatedAirspeed >= 285, $"the climb settled at {ac.IndicatedAirspeed:F0} KIAS, an exact 280 restored after the bound ended");
    }

    /// <summary>
    /// Guard, not a red-first pin: a 360 flown under an assigned speed keeps the maneuver's holding speed for its whole
    /// length, so a design that restores the assignment every tick (rather than behind the cap, as this one does) cannot
    /// drag the aircraft back to it mid-maneuver.
    /// </summary>
    [Fact]
    public void AssignedSpeed_IsNotResumedDuringA360()
    {
        AircraftState ac = A320At(altitude: 6_000, ias: 240);
        Assert.True(Send(ac, "SPD 210").Success);
        double holding = AircraftPerformance.HoldingSpeed("A320", ac.Altitude);
        Assert.True(holding < 208, $"premise: a 360 at 6,000 ft slows below the assigned 210 (holding speed {holding:F0})");

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);
        var turn = new MakeTurnPhase { Direction = TurnDirection.Right, TargetDegrees = 360 };
        turn.OnStart(ctx);

        bool done = false;
        bool atHolding = false;
        double iasBeforeDone = double.NaN;
        double fastestWhileTurning = double.MinValue;
        for (int second = 0; (second < 400) && !done; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                FlightPhysics.Update(ac, 0.25);
            }

            iasBeforeDone = ac.IndicatedAirspeed;
            atHolding |= ac.IndicatedAirspeed <= holding + SpeedSnapKts;
            if (atHolding)
            {
                fastestWhileTurning = Math.Max(fastestWhileTurning, ac.IndicatedAirspeed);
            }

            done = turn.OnTick(ctx);
        }

        _output.WriteLine($"360 finished at {iasBeforeDone:F0} KIAS (holding {holding:F0}, peak {fastestWhileTurning:F0})");
        Assert.True(done, "premise: the 360 completes");
        Assert.True(atHolding, "premise: the maneuver slows to its holding speed");
        Assert.Equal(210, Assert.IsType<double>(ac.Targets.AssignedSpeed), 0.5);
        Assert.InRange(iasBeforeDone, holding - SpeedSnapKts, holding + SpeedSnapKts);
        Assert.True(
            fastestWhileTurning <= holding + 5,
            $"the maneuver accelerated to {fastestWhileTurning:F0} KIAS, above holding speed {holding:F0}: the assignment was restored mid-turn"
        );
    }

    // ---- SPDN lifts every speed limit ----------------------------------------------------

    [Fact]
    public void ForceSpeed_PastVmo_IsFlownPastVmo()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        double slowest = double.MaxValue;
        FlySeconds(ac, 120, a => slowest = Math.Min(slowest, a.IndicatedAirspeed));

        Assert.True(slowest >= 360 - SpeedSnapKts, $"SPDN 360 at FL200 slowed to {slowest:F1} KIAS, held to VMO {A320VmoKts}");
        Assert.Equal(360, ac.IndicatedAirspeed, SpeedSnapKts);
    }

    /// <summary>Near Needles, AZ: below 10,000 ft and under no Class B shelf, so 91.117(a)'s 250 kt is the only legal cap.</summary>
    [Fact]
    public void ForceSpeed_Below10k_IsFlownPast250()
    {
        AircraftState ac = A320At(altitude: 8_000, ias: 250);
        Assert.Equal(250, FlightPhysics.RegulatorySpeedLimit(ac));
        Assert.True(Send(ac, "SPDN 300").Success);

        FlySeconds(ac, 120, _ => { });

        Assert.Equal(300, ac.IndicatedAirspeed, SpeedSnapKts);
    }

    [Fact]
    public void ForceSpeedRfas_SetsTheFlag()
    {
        AircraftState ac = A320At(altitude: 5_000, ias: 220);

        CommandResult result = CommandDispatcher.Dispatch(new ForceFinalApproachSpeedCommand(), ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Targets.SpeedLimitsLifted);
    }

    [Theory]
    [InlineData("SPD 280")]
    [InlineData("SPEEDF 280")]
    [InlineData("MACH .60")]
    [InlineData("RFAS")]
    [InlineData("DSR")]
    [InlineData("RNS")]
    [InlineData("CFIX SUNOL 200 280")]
    [InlineData("WARP SUNOL 270 200 280")]
    public void SpeedActAfterForceSpeed_ClearsTheFlag(string act)
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);
        Assert.True(ac.Targets.SpeedLimitsLifted, "premise: SPDN sets the flag");

        CommandResult result = Send(ac, act);

        Assert.True(result.Success, $"{act}: {result.Message}");
        Assert.False(ac.Targets.SpeedLimitsLifted, $"{act} left the SPDN flag set");
    }

    [Fact]
    public void RefusedSpeed_AfterForceSpeed_KeepsTheFlag()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = Send(ac, "SPD 400");

        Assert.False(result.Success, $"premise: SPD 400 past VMO is refused, got '{result.Message}'");
        Assert.True(ac.Targets.SpeedLimitsLifted);
        Assert.Equal(360, ac.Targets.AssignedSpeed);
    }

    [Theory]
    [InlineData("CM 240")]
    [InlineData("DM 160")]
    public void AltitudeClearance_AfterForceSpeed_KeepsTheFlag(string clearance)
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = Send(ac, clearance);

        Assert.True(result.Success, $"{clearance}: {result.Message}");
        Assert.True(ac.Targets.SpeedLimitsLifted, $"{clearance} cleared the SPDN flag");
    }

    [Fact]
    public void ConstrainedDirectWithASpeed_AfterForceSpeed_ClearsTheFlag()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = CommandDispatcher.Dispatch(
            SpeedEnvelopeRefusalTests.ConstrainedDirectToSunol(20_000, 300),
            ac,
            TestDispatch.Context(new SerializableRandom(1))
        );

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Targets.SpeedLimitsLifted);
    }

    [Fact]
    public void AltitudeOnlyCrossFix_AfterForceSpeed_KeepsTheFlag()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = Send(ac, "CFIX SUNOL 200");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Targets.SpeedLimitsLifted);
    }

    /// <summary>
    /// SPDN lifts 14 CFR 91.117(c)'s 200 kt too: a jet at 3,000 ft under the San Francisco Class B shelf, the first point
    /// of a grid around KSFO that the airspace data puts under a shelf.
    /// </summary>
    [Fact]
    public void ForceSpeed_UnderAClassBShelf_IsFlownPast200()
    {
        const double altitude = 3_000;
        LatLon? underShelf = null;
        for (double lat = 37.2; (lat <= 37.9) && (underShelf is null); lat += 0.05)
        {
            for (double lon = -122.6; (lon <= -121.9) && (underShelf is null); lon += 0.05)
            {
                var candidate = new LatLon(lat, lon);
                if (Yaat.Sim.Data.Airspace.AirspaceDatabase.Default.IsUnderClassBShelf(candidate, altitude))
                {
                    underShelf = candidate;
                }
            }
        }

        Assert.True(underShelf is not null, "premise: some point around KSFO at 3,000 ft is under a Class B shelf");
        AircraftState ac = A320At(altitude, ias: 200);
        ac.Position = underShelf.Value;
        Assert.Equal(200, FlightPhysics.RegulatorySpeedLimit(ac));
        Assert.True(Send(ac, "SPDN 260").Success);

        FlySeconds(ac, 30, _ => { });

        Assert.Equal(260, ac.IndicatedAirspeed, SpeedSnapKts);
    }

    /// <summary>
    /// "SPDN 210, CAPP I30" in one block: the approach clearance cancels the speed and puts back the one restated with it,
    /// the SPDN flag included. A generated jet on Oakland's real OAKES3 STAR, past its entry fix.
    /// </summary>
    [Fact]
    public void ForceSpeedThenApproachClearanceInOneBlock_KeepsTheFlag()
    {
        if (Yaat.Sim.Tests.Simulation.DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState ac = harness.Aircraft;
        string entryFix = ac.Targets.NavigationRoute[0].Name;
        for (int t = 0; (t < 1800) && ac.Targets.NavigationRoute.Exists(f => f.Name.Equals(entryFix, StringComparison.OrdinalIgnoreCase)); t++)
        {
            harness.Engine.TickOneSecond();
        }

        CommandResult result = harness.Engine.SendCommand(ac.Callsign, "SPDN 210, CAPP I30");

        Assert.True(result.Success, result.Message);
        Assert.Equal(210, ac.Targets.AssignedSpeed);
        Assert.True(ac.Targets.SpeedLimitsLifted);
    }
}

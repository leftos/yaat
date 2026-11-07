using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1423: the sourced per-type speed limits in AircraftProfileOverrides.json. Each type flies its own type
/// certificate's Vmo/Mmo (the Vmo at their altitude where the TCDS bands it, checked at 5,000 ft), each helicopter its own Vne, and a
/// fixed-wing type with neither an FAA approach speed nor an override flies its profile landing speed as its approach
/// speed and minimum. Figures: FAA TCDS via OpenAirDocs, Eurocontrol APD for the approach speeds.
/// </summary>
public sealed class SpeedEnvelopeDataCorrectionTests
{
    private const double CheckAltitudeFt = 5_000;
    private const double TurbopropClassMmo = 0.55;

    public SpeedEnvelopeDataCorrectionTests() => TestVnasData.EnsureInitialized();

    public static TheoryData<string, double, double?> SourcedTypes =>
        new()
        {
            { "CL60", 300, 0.85 },
            { "LJ35", 300, 0.81 },
            { "LJ45", 330, 0.81 },
            { "FA20", 390, 0.88 },
            { "FA50", 370, 0.86 },
            { "GLF3", 340, 0.85 },
            { "GLEX", 300, 0.89 },
            { "GLF6", 340, 0.925 },
            { "JS20", 215, null },
            { "A342", 330, 0.86 },
            { "A343", 330, 0.86 },
            { "A345", 330, 0.86 },
            { "A346", 330, 0.86 },
            { "B762", 360, 0.86 },
            { "B763", 360, 0.86 },
            { "B764", 360, 0.86 },
            { "CRJ1", 330, 0.85 },
            { "CRJ2", 330, 0.85 },
            { "CRJ7", 330, 0.85 },
            { "CRJ9", 330, 0.84 },
            { "B752", 350, 0.86 },
            { "B753", 350, 0.86 },
            { "MD11", 357.5, 0.87 },
            { "E170", 300, 0.82 },
            { "B74S", 375, 0.92 },
            { "L101", 375, 0.90 },
            { "F100", 320, 0.77 },
            { "F70", 320, 0.77 },
            { "B463", 250, 0.73 },
            { "D228", 199, null },
            { "DH8C", 243, null },
            { "DH8D", 245, 0.60 },
            { "LJ60", 300, 0.81 },
            { "GLF5", 340, 0.885 },
            { "K35E", 350, 0.90 },
            { "C135", 350, 0.90 },
            { "C141", 350, 0.825 },
            { "C5M", 350, 0.825 },
            { "C208", 175, null },
            { "PC12", 240, 0.48 },
            { "TBM7", 271, null },
            { "TBM8", 271, null },
            { "TBM9", 270, null },
            { "KODI", 180, null },
            { "P46T", 188, null },
            { "P180", 260, 0.70 },
            { "B190", 248, 0.48 },
            { "B350", 263, 0.58 },
            { "BE20", 260, 0.52 },
        };

    /// <summary>
    /// YAAT-1443: a piston stores its Vno (top of the green arc; pistons have no Vmo) and has no Mmo, so SMAX holds Vno less
    /// the margin at every altitude. Each figure is the stored one, unraised by the profile floor, except M20P (below).
    /// </summary>
    public static TheoryData<string, double> PistonTypes =>
        new()
        {
            { "C172", 129 },
            { "C182", 140 },
            { "P28A", 121 },
            { "SR22", 179 },
            { "SR20", 164 },
            // M20P stores Vno 174 KIAS, but its profile cruise (237 KTAS, a turbo Mooney's figure; YAAT-1956 fixes it)
            // floor-raises the unbanded Vmo to 174.7 kt, a raise of under 1 kt that YAAT-1443 accepts.
            { "M20P", 174.7 },
            { "BE36", 165 },
            { "BE58", 195 },
            { "PA34", 166 },
            { "PA31", 188 },
            { "C310", 181 },
            { "DA42", 155 },
            { "DA62", 160 },
            { "C421", 201 },
            { "C210", 165 },
            { "BE33", 165 },
            { "C152", 111 },
            { "C180", 139 },
            { "AA5", 143 },
        };

    [Theory]
    [MemberData(nameof(PistonTypes))]
    public void Pistons_FlyTheirVno_WithNoMmo(string type, double vnoKts)
    {
        Assert.Equal(vnoKts, AircraftPerformance.MaxOperatingIasKts(type, CheckAltitudeFt)!.Value, 1);
        Assert.Null(AircraftPerformance.MaxOperatingMach(type));
    }

    [Theory]
    [MemberData(nameof(SourcedTypes))]
    public void SourcedTypes_FlyTheirTcdsVmoAndMmo(string type, double vmoKts, double? mmo)
    {
        // vmoKts is the TCDS Vmo at CheckAltitudeFt: for a type whose TCDS bands Vmo by altitude, the band there.
        // A turboprop whose TCDS gives no Mmo keeps its TP2M class Mmo.
        double expectedMach = mmo ?? TurbopropClassMmo;
        double? flownMach = AircraftPerformance.MaxOperatingMach(type);
        Assert.NotNull(flownMach);
        Assert.Equal(expectedMach, flownMach.Value, 3);

        double expectedIas = Math.Min(vmoKts, WindInterpolator.MachToIas(expectedMach, CheckAltitudeFt));
        Assert.Equal(expectedIas, AircraftPerformance.MaxOperatingIasKts(type, CheckAltitudeFt)!.Value, 1);
    }

    /// <summary>
    /// A type whose certificate limits Vmo by altitude flies the band value there (the B463 band tests' accessor); F50's
    /// 206 kt at 25,000 ft is the straight line from 224 kt at 21,000 ft to 206 kt at 25,000 ft, so 23,000 ft is 215 kt.
    /// </summary>
    [Theory]
    [InlineData("LJ60", 5_000, 300)]
    [InlineData("LJ60", 15_000, 340)]
    [InlineData("F50", 10_000, 224)]
    [InlineData("F50", 23_000, 215)]
    [InlineData("S22T", 10_000, 179)]
    [InlineData("S22T", 21_250, 165.5)]
    public void BandedTypes_FlyTheirBandAtTheCheckedAltitude(string type, double altitudeFt, double expectedKts) =>
        Assert.Equal(expectedKts, AircraftPerformance.VmoAtKts(type, altitudeFt)!.Value, 0.5);

    [Theory]
    [InlineData("H60", 193)]
    [InlineData("R22", 98)]
    [InlineData("R44", 130)]
    [InlineData("B06", 130)]
    [InlineData("B407", 140)]
    [InlineData("S76", 155)]
    [InlineData("AS50", 155)]
    [InlineData("EC35", 155)]
    [InlineData("EC45", 150)]
    [InlineData("A139", 167)]
    [InlineData("A109", 168)]
    [InlineData("R66", 140)]
    [InlineData("B429", 155)]
    [InlineData("EC30", 155)]
    [InlineData("AS65", 175)]
    [InlineData("B412", 140)]
    [InlineData("S92", 165)]
    public void Helicopters_FlyTheirOwnVne(string type, double vneKts)
    {
        Assert.Null(AircraftPerformance.MaxOperatingMach(type));
        Assert.Equal(vneKts, AircraftPerformance.MaxOperatingIasKts(type, CheckAltitudeFt)!.Value, 1);
    }

    /// <summary>
    /// YAAT-1443: each helicopter given a sourced Vne is categorized a helicopter, so SMAX holds 0.9 × Vne rather than the
    /// fixed-wing Vne less 10 kt (an unknown type falls back to Jet).
    /// </summary>
    [Theory]
    [InlineData("EC45")]
    [InlineData("A139")]
    [InlineData("A109")]
    [InlineData("R66")]
    [InlineData("B429")]
    [InlineData("EC30")]
    [InlineData("AS65")]
    [InlineData("B412")]
    [InlineData("S92")]
    public void SourcedHelicopters_AreCategorizedHelicopter(string type) =>
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize(type));

    /// <summary>
    /// YAAT-1443: KODI borrows TBM7's profile through the sibling map; its override holds the climb, cruise and descent
    /// at or under Vmo 180 less the 10 kt SMAX margin at every altitude to the ceiling, so SMAX never slows it.
    /// </summary>
    [Fact]
    public void Kodi_ProfileSchedule_StaysUnderVmoLessTheMargin()
    {
        AircraftProfile profile = AircraftProfileDatabase.Get("KODI")!;
        for (double alt = 0; alt <= profile.Ceiling; alt += 500)
        {
            double climb = AircraftPerformance.ClimbSpeed("KODI", AircraftCategory.Turboprop, alt);
            double descent = AircraftPerformance.DescentSpeed("KODI", AircraftCategory.Turboprop, alt);
            double cruise = AircraftPerformance.DefaultSpeed("KODI", AircraftCategory.Turboprop, alt, targetAltitude: null);
            Assert.True(
                Math.Max(climb, Math.Max(descent, cruise)) <= 170,
                $"at {alt} ft: climb {climb:F1}, descent {descent:F1}, cruise {cruise:F1}"
            );
        }

        Assert.True(profile.HoldingSpeed <= 170, $"holding {profile.HoldingSpeed}");
    }

    [Fact]
    public void Kc135_AcceptsSpeedsDownToItsApdVat()
    {
        // Eurocontrol APD K35E Landing Vat: 135 kt.
        Assert.Equal(135, AircraftPerformance.MinOperatingIasKts("K35E", AircraftCategory.Jet), 1);

        AircraftState ac = Airborne("K35E", altitude: 4_000, ias: 210);
        CommandResult result = Send(ac, "SPD 150");

        Assert.True(result.Success, result.Message);
        Assert.Equal(150, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void TypeWithFaaFigure_MinimumIsItsFaaApproachSpeed() =>
        Assert.Equal(144, AircraftPerformance.MinOperatingIasKts("B738", AircraftCategory.Jet), 1);

    [Theory]
    [InlineData("C5M", 135)]
    [InlineData("F18", 140)]
    public void TypeWithoutFaaOrOverride_ApproachAndMinimumAreTheLandingSpeed(string type, double landingSpeedKts)
    {
        // A fixed-wing type with a profile, no FAA ACD approach speed and no approach-speed override flies its
        // profile landing speed (the Eurocontrol APD Vat) as its approach speed and its minimum.
        AircraftProfile? profile = AircraftProfileDatabase.Get(type);
        Assert.NotNull(profile);
        Assert.Null(FaaAircraftDatabase.Get(type)?.ApproachSpeedKnot);
        Assert.False(AircraftProfileDatabase.IsOverridden(type, nameof(AircraftProfile.FinalApproachSpeed)));
        Assert.Equal(landingSpeedKts, profile.LandingSpeed, 1);

        Assert.Equal(landingSpeedKts, AircraftPerformance.ApproachSpeed(type, AircraftCategory.Jet), 1);
        Assert.Equal(landingSpeedKts, AircraftPerformance.MinOperatingIasKts(type, AircraftCategory.Jet), 1);
    }

    [Fact]
    public void Helicopter_KeepsItsProfileApproachSpeed() =>
        // The H-60's profile landing speed 50 is no approach speed; it keeps its profile final approach speed.
        Assert.Equal(80, AircraftPerformance.ApproachSpeed("H60", AircraftCategory.Helicopter), 1);

    [Theory]
    [InlineData("H60", AircraftCategory.Helicopter)]
    [InlineData("VEH1", AircraftCategory.Jet)]
    public void HelicopterAndVehicle_KeepTheirProfileFasOverOnePointTwoMinimum(string type, AircraftCategory cat)
    {
        // Helicopters and ground vehicles are left out: each keeps its raw profile approach speed (80) and its
        // minimum of that figure over 1.2.
        Assert.Equal(80, AircraftPerformance.ApproachSpeed(type, cat), 1);
        Assert.Equal(80 / 1.2, AircraftPerformance.MinOperatingIasKts(type, cat), 1);
    }

    [Fact]
    public void Pa28_TakesP28aFaaApproachSpeed()
    {
        // PA28 has no FAA ACD record of its own; it resolves to P28A's record through the sibling map.
        Assert.False(FaaAircraftDatabase.Records.ContainsKey("PA28"));
        Assert.Equal(70, AircraftPerformance.ApproachSpeed("PA28", AircraftCategory.Piston), 1);
    }

    [Fact]
    public void E170_DescentSpeedNearCeiling_IsItsDescentMach()
    {
        AircraftProfile profile = AircraftProfileDatabase.Get("E170")!;

        double atCeiling = AircraftPerformance.DescentSpeed("E170", AircraftCategory.Jet, profile.Ceiling);

        Assert.Equal(WindInterpolator.MachToIas(0.78, profile.Ceiling), atCeiling, 1);
    }

    [Fact]
    public void Js20_DescentSpeedAtCeiling_Is10KtUnderItsTcdsVmoThere()
    {
        // TCDS A21EU: Vmo 180 kt at 25,000 ft, the JS20's ceiling; the descent flies 10 kt under it (descentSpeedInitial 170).
        AircraftProfile profile = AircraftProfileDatabase.Get("JS20")!;

        double atCeiling = AircraftPerformance.DescentSpeed("JS20", AircraftCategory.Turboprop, profile.Ceiling);

        Assert.Equal(170, atCeiling, 1);
    }

    [Fact]
    public void Dh8c_DescentSchedule_StaysUnderItsTcdsBands()
    {
        // TCDS A13NM (DHC-8-300): Vmo 243 kt to 17,000 ft and 214 kt at 25,000 ft, the ceiling; the descent flies 10 kt
        // under each.
        Assert.Equal(233, AircraftPerformance.DescentSpeed("DH8C", AircraftCategory.Turboprop, 10_000), 1);
        Assert.Equal(204, AircraftPerformance.DescentSpeed("DH8C", AircraftCategory.Turboprop, 25_000), 1);
    }

    private static AircraftState Airborne(string type, double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "PACK11",
            AircraftType = type,
            Position = new LatLon(37.62, -122.20),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KSFO",
                Destination = "KTCM",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(280);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(
            parsed.Value!,
            aircraft,
            TestDispatch.Context(new SerializableRandom(1), isScenarioScripted: false)
        );
    }
}

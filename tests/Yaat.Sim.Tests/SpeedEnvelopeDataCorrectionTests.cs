using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1423: the sourced per-type speed limits in AircraftProfileOverrides.json. Each type flies its own type
/// certificate's Vmo/Mmo (the band maximum where the TCDS bands Vmo by altitude), each helicopter its own Vne, and a
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
            { "CL60", 348, 0.85 },
            { "LJ35", 350, 0.81 },
            { "LJ45", 330, 0.81 },
            { "FA20", 390, 0.88 },
            { "FA50", 370, 0.86 },
            { "GLF3", 340, 0.85 },
            { "GLEX", 340, 0.89 },
            { "GLF6", 340, 0.925 },
            { "JS20", 215, null },
            { "A342", 330, 0.86 },
            { "A343", 330, 0.86 },
            { "A345", 330, 0.86 },
            { "A346", 330, 0.86 },
            { "B762", 360, 0.86 },
            { "B763", 360, 0.86 },
            { "B764", 360, 0.86 },
            { "CRJ1", 335, 0.85 },
            { "CRJ2", 335, 0.85 },
            { "CRJ7", 335, 0.85 },
            { "CRJ9", 335, 0.85 },
            { "B752", 350, 0.86 },
            { "B753", 350, 0.86 },
            { "MD11", 365, 0.87 },
            { "E170", 320, 0.82 },
            { "B74S", 375, 0.92 },
            { "L101", 375, 0.90 },
            { "F100", 320, 0.77 },
            { "F70", 320, 0.77 },
            { "B463", 295, 0.73 },
            { "D228", 199, null },
            { "DH8C", 243, null },
            { "DH8D", 286, 0.60 },
        };

    [Theory]
    [MemberData(nameof(SourcedTypes))]
    public void SourcedTypes_FlyTheirTcdsVmoAndMmo(string type, double vmoKts, double? mmo)
    {
        // A turboprop whose TCDS gives no Mmo keeps its TP2M class Mmo.
        double expectedMach = mmo ?? TurbopropClassMmo;
        double? flownMach = AircraftPerformance.MaxOperatingMach(type);
        Assert.NotNull(flownMach);
        Assert.Equal(expectedMach, flownMach.Value, 3);

        double expectedIas = Math.Min(vmoKts, WindInterpolator.MachToIas(expectedMach, CheckAltitudeFt));
        Assert.Equal(expectedIas, AircraftPerformance.MaxOperatingIasKts(type, CheckAltitudeFt)!.Value, 1);
    }

    [Theory]
    [InlineData("R22", 98)]
    [InlineData("R44", 130)]
    [InlineData("B06", 130)]
    [InlineData("B407", 140)]
    [InlineData("S76", 155)]
    [InlineData("AS50", 155)]
    [InlineData("EC35", 155)]
    public void Helicopters_FlyTheirOwnVne(string type, double vneKts)
    {
        Assert.Null(AircraftPerformance.MaxOperatingMach(type));
        Assert.Equal(vneKts, AircraftPerformance.MaxOperatingIasKts(type, CheckAltitudeFt)!.Value, 1);
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
    public void Js20_DescentSpeedAtCeiling_IsItsTcdsVmoThere()
    {
        // TCDS A21EU: Vmo 180 kt at 25,000 ft, the JS20's ceiling.
        AircraftProfile profile = AircraftProfileDatabase.Get("JS20")!;

        double atCeiling = AircraftPerformance.DescentSpeed("JS20", AircraftCategory.Turboprop, profile.Ceiling);

        Assert.Equal(180, atCeiling, 1);
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

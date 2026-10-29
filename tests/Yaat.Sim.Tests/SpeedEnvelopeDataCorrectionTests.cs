using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1423: the sourced per-type speed limits in AircraftProfileOverrides.json. Each type flies its own type
/// certificate's Vmo/Mmo (the band maximum where the TCDS bands Vmo by altitude), each helicopter its own Vne, and a
/// type with neither an FAA approach speed nor an override takes its profile final approach speed over 1.2 as its
/// minimum. Figures: FAA TCDS via OpenAirDocs, Eurocontrol APD for the approach speeds.
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

    [Fact]
    public void Dh8d_FliesItsTcdsVmo()
    {
        // TCDS A13NM gives the Q400 no Mmo. Its TP2M class Mmo is lifted by the floor of the DH8C profile it flies, so
        // only the floor's direction is pinned here.
        double mach = AircraftPerformance.MaxOperatingMach("DH8D")!.Value;
        Assert.True(mach >= TurbopropClassMmo, $"DH8D Mmo {mach} is below its class figure");

        double expectedIas = Math.Min(286, WindInterpolator.MachToIas(mach, CheckAltitudeFt));
        Assert.Equal(expectedIas, AircraftPerformance.MaxOperatingIasKts("DH8D", CheckAltitudeFt)!.Value, 1);
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

    [Fact]
    public void TypeWithoutFaaOrOverride_MinimumIsProfileFasOverOnePointTwo()
    {
        // The C-5M has a profile, no FAA ACD record and no approach-speed override (Eurocontrol APD has no C5M page).
        const string type = "C5M";
        AircraftProfile? profile = AircraftProfileDatabase.Get(type);
        Assert.NotNull(profile);
        Assert.Null(FaaAircraftDatabase.Get(type)?.ApproachSpeedKnot);
        Assert.False(AircraftProfileDatabase.IsOverridden(type, nameof(AircraftProfile.FinalApproachSpeed)));

        Assert.Equal(profile.FinalApproachSpeed / 1.2, AircraftPerformance.MinOperatingIasKts(type, AircraftCategory.Jet), 3);
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

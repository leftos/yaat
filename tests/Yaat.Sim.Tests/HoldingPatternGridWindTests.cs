using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// A hold's wind corrections use the wind at the aircraft's own position: under a profile whose winds come only from the GFS
/// grid, the outbound heading (AIM 5-3-8(j)(8)(c) triple drift) and the outbound leg time (AIM 5-3-8 timing) correct for each
/// fix's grid wind, where the layers alone would give no wind at all.
/// </summary>
public class HoldingPatternGridWindTests
{
    private const string Callsign = "HLD01";
    private const double AltitudeFt = 10_000;
    private const double Ias = 230;

    private static readonly LatLon Sfo = new(37.62, -122.38);

    public HoldingPatternGridWindTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static WeatherProfile GridOnly() => new() { WindGrid = GriddedWindSamplingTests.Field };

    private static WindAtAltitude GridWindAt(LatLon fix, WeatherProfile weather) =>
        WindInterpolator.GetWindAt(
            weather,
            new WindSamplePoint(fix, MagneticDeclination.GetDeclination(fix)),
            AltitudeFt,
            0,
            WindVariation.PhaseSecondsFor(Callsign)
        );

    [Fact]
    public void DirectEntry_OutboundHeading_CorrectsForTheGridWindAtTheFix()
    {
        WeatherProfile weather = GridOnly();
        const int InboundCourse = 360;

        double atSfo = OutboundHeadingAt(Sfo, weather, InboundCourse);
        double atReno = OutboundHeadingAt(new LatLon(39.5, -119.8), weather, InboundCourse);

        Assert.True(Math.Abs(atSfo - atReno) > 0.2, $"outbound {atSfo:F2} at SFO and {atReno:F2} at Reno");
    }

    /// <summary>
    /// The outbound leg is timed so the inbound leg takes a minute in the grid's wind at the fix: with the inbound course into
    /// that wind, the outbound leg is shortened by the inbound headwind (AIM 5-3-8), where no wind would leave it a minute.
    /// </summary>
    [Fact]
    public void Outbound_LegTime_CorrectsForTheGridHeadwindAtTheFix()
    {
        WeatherProfile weather = GridOnly();
        WindAtAltitude wind = GridWindAt(Sfo, weather);
        Assert.True(wind.SpeedKts > 10, $"the grid gives only {wind.SpeedKts:F1} kt at the fix");
        int inboundCourse = (int)Math.Round(wind.DirectionDeg) % 360;
        (HoldingPatternPhase hold, PhaseContext ctx) = StartAtFix(Sfo, weather, inboundCourse);

        hold.OnTick(ctx);
        ctx.Aircraft.TrueHeading = Assert.IsType<TrueHeading>(ctx.Targets.TargetTrueHeading);
        hold.OnTick(ctx);
        int outboundTicks = 0;
        double inboundTarget = new TrueHeading(inboundCourse).Degrees;
        while (Assert.IsType<TrueHeading>(ctx.Targets.TargetTrueHeading).Degrees != inboundTarget)
        {
            Assert.True(outboundTicks < 400, "the outbound leg never ended");
            hold.OnTick(ctx);
            outboundTicks++;
        }

        Assert.True(outboundTicks < 60, $"the outbound leg ran {outboundTicks} s, as if in no wind");
    }

    /// <summary>
    /// A direct-entry hold at <paramref name="fix"/> with the aircraft at the fix, started; the first tick turns it outbound.
    /// </summary>
    private static (HoldingPatternPhase Hold, PhaseContext Ctx) StartAtFix(LatLon fix, WeatherProfile weather, int inboundCourse)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = fix,
            TrueHeading = new TrueHeading(inboundCourse),
            Altitude = AltitudeFt,
            IndicatedAirspeed = Ias,
            Declination = MagneticDeclination.GetDeclination(fix),
        };
        var hold = new HoldingPatternPhase
        {
            FixName = "GRID",
            FixLat = fix.Lat,
            FixLon = fix.Lon,
            InboundCourse = inboundCourse,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
            Entry = HoldingEntry.Direct,
        };
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
            Weather = weather,
            ScenarioElapsedSeconds = 0,
        };

        hold.OnStart(ctx);
        return (hold, ctx);
    }

    /// <summary>
    /// The corrected outbound heading a direct-entry hold at <paramref name="fix"/> turns to, after checking it is the
    /// triple-drift correction for the wind sampled at the fix with the aircraft's declination.
    /// </summary>
    private static double OutboundHeadingAt(LatLon fix, WeatherProfile weather, int inboundCourse)
    {
        (HoldingPatternPhase hold, PhaseContext ctx) = StartAtFix(fix, weather, inboundCourse);
        hold.OnTick(ctx);

        WindAtAltitude wind = GridWindAt(fix, weather);
        Assert.True(wind.SpeedKts > 0, "the grid gives no wind at the fix");
        double wca = WindInterpolator.ComputeWindCorrectionAngle(
            inboundCourse,
            WindInterpolator.IasToTas(Ias, AltitudeFt),
            wind.DirectionDeg,
            wind.SpeedKts
        );
        double expected = new TrueHeading(inboundCourse + 180 - (3.0 * wca)).Degrees;

        TrueHeading target = Assert.IsType<TrueHeading>(ctx.Targets.TargetTrueHeading);
        Assert.Equal(expected, target.Degrees, 9);
        return target.Degrees;
    }
}

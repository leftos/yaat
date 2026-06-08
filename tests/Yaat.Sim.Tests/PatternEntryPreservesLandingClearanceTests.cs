using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
/// <summary>
/// A pattern-entry command (EF/ERB/ELB/...) rebuilds the whole phase chain and
/// picks its terminal (LandingPhase vs TouchAndGoPhase). That terminal must
/// follow the controller's standing landing clearance, NOT the transient
/// pattern turn-direction the entry itself stamps onto the chain.
///
/// Regression for N713UP (BE36) at OAK 28R: the controller issued <c>CLAND</c>
/// (full-stop), then <c>EF 28R</c>, then <c>ERB 28R</c>. The first pattern entry
/// stamped <c>phases.TrafficDirection</c>; the second read that non-null
/// direction and rebuilt the chain ending in <see cref="TouchAndGoPhase"/>, so
/// the aircraft touched and climbed away instead of landing — even though it was
/// still cleared to land. A touch-and-go must be requested explicitly with
/// <c>TG</c>/<c>COPT</c>, never as a side effect of re-entering the pattern.
/// </summary>
public class PatternEntryPreservesLandingClearanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public PatternEntryPreservesLandingClearanceTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(MakeOak28R()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo MakeOak28R()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            thresholdLat: 37.72152,
            thresholdLon: -122.20065,
            endLat: 37.73089,
            endLon: -122.21926,
            heading: 292,
            elevationFt: 9,
            lengthFt: 6213,
            widthFt: 150
        );
    }

    /// <summary>
    /// BE36 near TPA on the right (NNE) base for 28R — 2 nm outbound, 3 nm NNE of
    /// the extended centerline. AssignedRunway is set and a pending
    /// <see cref="LandingPhase"/> seeds the chain so CLAND/TG have a terminal to
    /// replace.
    /// </summary>
    private static AircraftState MakeOnRightBase(RunwayInfo runway)
    {
        var reciprocal = new TrueHeading((runway.TrueHeading.Degrees + 180) % 360);
        var centerline = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 2.0);
        var nneHeading = new TrueHeading((runway.TrueHeading.Degrees + 90) % 360);
        var pos = GeoMath.ProjectPoint(centerline.Lat, centerline.Lon, nneHeading, 3.0);

        var ac = new AircraftState
        {
            Callsign = "N713UP",
            AircraftType = "BE36",
            Position = new LatLon(pos.Lat, pos.Lon),
            Altitude = 1000,
            TrueHeading = new TrueHeading(202),
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        ac.Phases.Add(new LandingPhase());
        return ac;
    }

    private static Phase? Terminator(AircraftState ac)
    {
        return ac.Phases!.Phases.LastOrDefault(p =>
            p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
        );
    }

    [Fact]
    public void ClandThenTwoPatternEntries_KeepsLandingTerminal()
    {
        var runway = MakeOak28R();
        var ac = MakeOnRightBase(runway);

        var cland = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "28R" }, ac);
        Assert.True(cland.Success, cland.Message);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);

        // First pattern entry after CLAND is correct (Landing), but it stamps a
        // pattern turn-direction onto the chain — the precondition for the bug.
        var erb1 = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Right, PatternEntryLeg.Base, "28R", null);
        Assert.True(erb1.Success, erb1.Message);
        Assert.IsType<LandingPhase>(Terminator(ac));
        Assert.NotNull(ac.Phases.TrafficDirection);

        // Second pattern entry must NOT downgrade the standing landing clearance
        // to a touch-and-go. (Fails before the fix: terminal is TouchAndGoPhase.)
        var erb2 = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Right, PatternEntryLeg.Base, "28R", null);
        Assert.True(erb2.Success, erb2.Message);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);

        var terminator = Terminator(ac);
        _output.WriteLine($"Final terminator after second ERB: {terminator?.Name ?? "(none)"}");
        Assert.IsType<LandingPhase>(terminator);
    }

    [Fact]
    public void TouchAndGoClearance_PatternEntryKeepsTouchAndGo()
    {
        // Guard against over-correction: an explicit TG clearance must survive a
        // subsequent pattern entry as a TouchAndGoPhase.
        var runway = MakeOak28R();
        var ac = MakeOnRightBase(runway);

        var tg = PatternCommandHandler.TrySetupTouchAndGo(ac, PatternDirection.Right);
        Assert.True(tg.Success, tg.Message);
        Assert.Equal(ClearanceType.ClearedTouchAndGo, ac.Phases!.LandingClearance);

        var erb = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Right, PatternEntryLeg.Base, "28R", null);
        Assert.True(erb.Success, erb.Message);

        var terminator = Terminator(ac);
        _output.WriteLine($"Terminator after TG then ERB: {terminator?.Name ?? "(none)"}");
        Assert.IsType<TouchAndGoPhase>(terminator);
    }
}

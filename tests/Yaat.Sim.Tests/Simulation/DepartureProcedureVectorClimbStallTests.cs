using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: a lateral vector issued while a <see cref="DepartureProcedurePhase"/> is holding the
/// aircraft under a SID climb window (an ARINC-424 "at or below" / "between" crossing) must not strand
/// the climb at the window ceiling.
///
/// COAST9 RWY30 out of KOAK opens with a VD leg "heading 296° to OAK 4.0 DME, between 1400 and 2000".
/// <see cref="DepartureProcedurePhase.ApplyLegAltitudeCap"/> caps <c>TargetAltitude</c> at 2000 while
/// that leg is active. If the controller then vectors the aircraft off the SID (<c>FH</c>), the phase
/// yields: <c>CanAcceptCommand(FlyHeading)</c> is <see cref="CommandAcceptance.Allowed"/> and
/// <c>OnCommandAccepted</c> sets the override, so the next <c>OnTick</c> returns <c>true</c> and the phase
/// completes. But the override return path short-circuits <em>before</em> <c>Finish()</c>, which is the only
/// site that releases the leg cap back to the climb ceiling. Because the command took the <c>Allowed</c>
/// branch (not <c>ClearsPhase</c>), <c>ResumeAssignedAltitudeAfterPhaseClear</c> never runs either, and a
/// lateral vector is horizontal-only — so nothing restores the climb target. The aircraft levels at the
/// window ceiling (2000 ft) instead of continuing to its assigned/cruise altitude.
///
/// 7110.65 §4-5-7 / AIM 5-2-8: a heading vector is horizontal-only and does not cancel an altitude
/// clearance; vectoring an aircraft off a SID cancels the SID crossing restrictions, it does not pin the
/// aircraft at the intermediate window. The phase's own <c>Finish()</c> encodes exactly this (it restores
/// <c>TargetAltitude = _climbCeiling</c>); the override path simply skips it.
/// </summary>
[Collection("NavDbMutator")]
public class DepartureProcedureVectorClimbStallTests
{
    private const double WindowCeilingFt = 2000.0;

    private readonly ITestOutputHelper _output;

    public DepartureProcedureVectorClimbStallTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo? Koak30()
    {
        RunwayInfo? phys = NavigationDatabase
            .Instance.GetRunways("KOAK")
            .FirstOrDefault(r => r.Id.End1 == "30" || r.Id.End2 == "30" || r.Designator.Contains("30", StringComparison.Ordinal));
        if (phys is null)
        {
            return null;
        }
        return new RunwayInfo
        {
            AirportId = "KOAK",
            Id = phys.Id,
            Designator = "30",
            Lat1 = phys.Lat1,
            Lon1 = phys.Lon1,
            Elevation1Ft = phys.Elevation1Ft,
            TrueHeading1 = phys.TrueHeading1,
            Lat2 = phys.Lat2,
            Lon2 = phys.Lon2,
            Elevation2Ft = phys.Elevation2Ft,
            TrueHeading2 = phys.TrueHeading2,
            WidthFt = phys.WidthFt,
        };
    }

    [Fact]
    public void VectorOffSidInsideClimbWindow_ResumesClimb_DoesNotStallAtWindowCeiling()
    {
        RunwayInfo? rwy = Koak30();
        if (rwy is null)
        {
            _output.WriteLine("KOAK RWY30 not in test nav data — skipping.");
            return;
        }

        (double Lat, double Lon)? oakPos = NavigationDatabase.Instance.GetFixPosition("OAK");
        if (oakPos is null || NavigationDatabase.Instance.GetSid("KOAK", "COAST9") is null)
        {
            _output.WriteLine("COAST9 / OAK not in test nav data — skipping.");
            return;
        }
        var oak = new LatLon(oakPos.Value.Lat, oakPos.Value.Lon);

        var ac = new AircraftState
        {
            Callsign = "SWA1822",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSAN",
                Route = "COAST9 MCKEY LAX COMIX2",
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };

        var holding = new HoldingInPositionPhase();
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        CommandResult cto = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: null,
            TestDispatch.Context(Random.Shared),
            NullLogger.Instance
        );
        Assert.True(cto.Success, cto.Message);

        // Airborne just past the DER, above the 400 ft AGL turn floor.
        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.2);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + 450;
        ac.IndicatedAirspeed = 170;

        AircraftCategory cat = AircraftCategorization.Categorize(ac.AircraftType);
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = cat,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
        ac.Phases.SkipTo<InitialClimbPhase>(ctx);

        // Fly until the aircraft is established in the DepartureProcedurePhase climb window: inside OAK
        // 4.0 DME and leveled at/near the 2000 ft ceiling (the VD "between 1400 and 2000" leg is active).
        bool reachedWindow = false;
        double altAtVector = 0;
        for (int t = 0; t < 360 && !reachedWindow; t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0);

            double dme = GeoMath.DistanceNm(ac.Position, oak);
            bool inProcedure = ac.Phases.CurrentPhase is DepartureProcedurePhase;
            bool cappedAtWindow = (ac.Targets.TargetAltitude ?? double.MaxValue) <= WindowCeilingFt + 50;

            if (inProcedure && dme < 3.5 && ac.Altitude >= WindowCeilingFt - 150 && cappedAtWindow)
            {
                reachedWindow = true;
                altAtVector = ac.Altitude;
                _output.WriteLine(
                    $"reached window at t={t}: alt={ac.Altitude:F0} dmeOAK={dme:F2} tgtAlt={ac.Targets.TargetAltitude:F0} phase={ac.Phases.CurrentPhase?.Name}"
                );

                // Controller vectors the aircraft off the SID — a lateral-only instruction.
                ParseResult<ParsedCommand> parsed = CommandParser.Parse("FH 270");
                Assert.True(parsed.IsSuccess, parsed.Reason);
                CommandResult vector = CommandDispatcher.Dispatch(parsed.Value!, ac, TestDispatch.Context(Random.Shared));
                Assert.True(vector.Success, $"FH 270 was refused: {vector.Message}");
            }
        }

        Assert.True(reachedWindow, "aircraft never established in the COAST9 climb window under the DepartureProcedurePhase");
        Assert.True(altAtVector <= WindowCeilingFt + 100, $"precondition: expected the aircraft leveled at ~2000 ft, was {altAtVector:F0}");

        // After the vector the SID restriction is gone: the aircraft must resume its climb toward the
        // assigned/cruise altitude, not stall at the 2000 ft window ceiling.
        double maxAltAfterVector = ac.Altitude;
        for (int t = 0; t < 180; t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0);
            maxAltAfterVector = Math.Max(maxAltAfterVector, ac.Altitude);
            if (t % 20 == 0)
            {
                _output.WriteLine(
                    $"+{t}s alt={ac.Altitude:F0} vs={ac.VerticalSpeed:F0} tgtAlt={ac.Targets.TargetAltitude?.ToString() ?? "-"} phase={ac.Phases.CurrentPhase?.Name ?? "(none)"}"
                );
            }
            if (ac.Altitude > WindowCeilingFt + 1000)
            {
                break;
            }
        }

        _output.WriteLine($"maxAltAfterVector={maxAltAfterVector:F0} finalTgtAlt={ac.Targets.TargetAltitude?.ToString() ?? "-"}");

        Assert.True(
            maxAltAfterVector > WindowCeilingFt + 500,
            $"Aircraft stalled at {maxAltAfterVector:F0} ft after being vectored off COAST9 — the SID climb window "
                + $"({WindowCeilingFt:F0} ft) was never released, so the lateral vector silently capped the climb."
        );
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Issue #194 follow-up: when the simulated pilot voices a hold-short of a runway it must
/// <em>cross</em> (a combined designator like 15/33), the spoken report names only the single
/// runway end whose threshold the aircraft is nearest — "runway one five" or "runway three
/// three", never "one five slash three three". Exercised through the RPO show-pilot-speech
/// path, the only mode that voices the crossing report.
/// </summary>
public class HoldingShortCrossingNearestEndVoiceTests
{
    private static AircraftState MakeAircraft(LatLon position) =>
        new()
        {
            Callsign = "N22AB",
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", HasFlightPlan = true },
            Ground = new AircraftGroundOps { CurrentTaxiway = "C" },
            Phases = new PhaseList(),
        };

    private static PhaseContext RpoCtx(AircraftState ac) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = new AirportGroundLayout { AirportId = "OAK" },
            Logger = NullLogger.Instance,
            SoloTrainingMode = false,
            RpoShowPilotSpeech = true,
        };

    private static string? VoicedCrossingReport(LatLon position)
    {
        var ac = MakeAircraft(position);
        var phase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 1,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = "15/33",
            }
        );
        phase.OnStart(RpoCtx(ac));
        return ac.PendingPilotSpeech.SingleOrDefault();
    }

    [Fact]
    public void RpoCrossingReport_NamesNearestThresholdEnd()
    {
        TestVnasData.EnsureInitialized();
        var db = NavigationDatabase.InstanceOrNull;
        var end15 = db?.GetRunway("OAK", "15");
        var end33 = db?.GetRunway("OAK", "33");
        if (end15 is null || end33 is null)
        {
            return; // nav-data absent on this machine — skip
        }

        // Holding short near the 15 threshold → voice "runway one five", not the combined slash form.
        var near15 = VoicedCrossingReport(new LatLon(end15.ThresholdLatitude, end15.ThresholdLongitude));
        Assert.NotNull(near15);
        Assert.Contains("holding short runway one five", near15, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("three three", near15, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slash", near15, StringComparison.OrdinalIgnoreCase);

        // Holding short near the 33 threshold → voice "runway three three".
        var near33 = VoicedCrossingReport(new LatLon(end33.ThresholdLatitude, end33.ThresholdLongitude));
        Assert.NotNull(near33);
        Assert.Contains("holding short runway three three", near33, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("one five", near33, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slash", near33, StringComparison.OrdinalIgnoreCase);
    }
}

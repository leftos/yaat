using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A hold short of an active runway is named by its active end in the phase name ("Holding Short 1R") and the status line, the
/// end chosen as the hold starts and kept across a snapshot; with no end of the runway active the name keeps the combined id.
/// SFO with <c>01L 01R 28L 28R</c> active, the aircraft on the 19L threshold, so the nearer end is the inactive 19L.
/// </summary>
public class HoldingShortActiveEndNameTests
{
    private const string Runway = "01R/19L";

    private static readonly ActiveRunways SfoActive = ActiveRunways.Empty.With(
        "SFO",
        [.. new[] { "01L", "01R", "28L", "28R" }.Select(end => new ActiveRunway(end, ActiveRunwayUse.Both))]
    );

    private static (AircraftState Aircraft, HoldingShortPhase Hold) HeldAtThe19LThreshold(ActiveRunways activeRunways) =>
        HeldAtThe19LThreshold(activeRunways, HoldShortReason.RunwayCrossing);

    private static (AircraftState Aircraft, HoldingShortPhase Hold) HeldAtThe19LThreshold(ActiveRunways activeRunways, HoldShortReason reason)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo threshold = NavigationDatabase.Instance.GetRunway("SFO", "19L") ?? throw new InvalidOperationException("SFO 19L missing");
        var aircraft = new AircraftState
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            Position = new LatLon(threshold.ThresholdLatitude, threshold.ThresholdLongitude),
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            Phases = new PhaseList(),
        };
        aircraft.Ground.Layout = new AirportGroundLayout { AirportId = "SFO" };
        var hold = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 3,
                Reason = reason,
                TargetName = Runway,
            }
        );
        aircraft.Phases.Add(hold);
        aircraft.Phases.Start(
            new PhaseContext
            {
                ActiveRunways = activeRunways,
                RpoShowPilotSpeech = true,
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 0,
                Logger = NullLogger.Instance,
                GroundLayout = aircraft.Ground.Layout,
            }
        );
        return (aircraft, hold);
    }

    [Fact]
    public void ActiveRunway_PhaseNameAndStatusLineNameTheActiveEnd()
    {
        (AircraftState aircraft, HoldingShortPhase hold) = HeldAtThe19LThreshold(SfoActive);

        Assert.Equal("Holding Short 1R", hold.Name);
        Assert.Equal("Holding Short 1R", aircraft.Phases!.CurrentPhase!.Name);
        string status = AircraftStatusDescriber.Describe(aircraft, AircraftStatusContext.None).Text;
        Assert.Equal("Holding short 1R", status);
        Assert.DoesNotContain("19L", status, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotRoundTrip_KeepsTheActiveEnd()
    {
        (_, HoldingShortPhase hold) = HeldAtThe19LThreshold(SfoActive);

        string json = JsonSerializer.Serialize((HoldingShortPhaseDto)hold.ToSnapshot());
        HoldingShortPhaseDto dto = JsonSerializer.Deserialize<HoldingShortPhaseDto>(json)!;
        var restored = HoldingShortPhase.FromSnapshot(dto);

        Assert.Equal("Holding Short 1R", restored.Name);
    }

    /// <summary>A snapshot taken before the end was stored (no <c>DisplayEnd</c>) restores to the combined name.</summary>
    [Fact]
    public void SnapshotWithoutADisplayEnd_RestoresTheCombinedName()
    {
        (_, HoldingShortPhase hold) = HeldAtThe19LThreshold(SfoActive);

        JsonObject json = JsonSerializer.SerializeToNode((HoldingShortPhaseDto)hold.ToSnapshot())!.AsObject();
        json["DisplayEnd"] = null;
        HoldingShortPhaseDto dto = json.Deserialize<HoldingShortPhaseDto>()!;
        var restored = HoldingShortPhase.FromSnapshot(dto);

        Assert.Null(dto.DisplayEnd);
        Assert.Equal($"Holding Short {HoldShortTarget.Describe(Runway)}", restored.Name);
    }

    /// <summary>An ARWY change after the hold starts (19L made the only active end) leaves the name on the end chosen at start.</summary>
    [Fact]
    public void ArwyChangedMidHold_NameKeepsTheEndChosenAtStart()
    {
        (AircraftState aircraft, HoldingShortPhase hold) = HeldAtThe19LThreshold(SfoActive);
        ActiveRunways changed = ActiveRunways.Empty.With("SFO", [new ActiveRunway("19L", ActiveRunwayUse.Both)]);

        hold.OnTick(
            new PhaseContext
            {
                ActiveRunways = changed,
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 1,
                Logger = NullLogger.Instance,
                GroundLayout = aircraft.Ground.Layout,
            }
        );

        Assert.Equal("Holding Short 1R", hold.Name);
    }

    /// <summary>
    /// A typed <c>HS 19L</c> holds at the runway's bar under its combined id (the explicit hold re-arms the bar, so the typed end
    /// is not stored): with 1R active the pilot names the active end, "holding short of 1R", never "19L" or "1R/19L".
    /// </summary>
    [Fact]
    public void TypedHoldShortOfTheInactiveEnd_PilotNamesTheActiveEnd()
    {
        (AircraftState aircraft, _) = HeldAtThe19LThreshold(SfoActive, HoldShortReason.ExplicitHoldShort);

        PilotSpeechText speech = Assert.Single(aircraft.PendingPilotSpeech);
        Assert.Contains("holding short of 1R ", speech.Terminal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("19L", speech.Terminal, StringComparison.Ordinal);
    }

    [Fact]
    public void NoActiveRunways_PhaseNameKeepsTheCombinedId()
    {
        (_, HoldingShortPhase hold) = HeldAtThe19LThreshold(ActiveRunways.Empty);

        Assert.Equal($"Holding Short {HoldShortTarget.Describe(Runway)}", hold.Name);
        Assert.Contains("19L", hold.Name, StringComparison.Ordinal);
    }
}

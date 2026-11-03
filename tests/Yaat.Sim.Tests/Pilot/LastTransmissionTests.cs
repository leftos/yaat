using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The per-aircraft store of the last line it spoke: written when the frequency dequeues a line, kept through a snapshot
/// round trip, and left out of the JSON while empty (snapshot-zero stability).
/// </summary>
public sealed class LastTransmissionTests
{
    public LastTransmissionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Every row's spoken form differs from its terminal form and carries its own source kind, so a field read from the
    /// wrong sibling (or a defaulted one) fails. The three kinds the queue paths do not stamp differently today take
    /// distinct literal source kinds through the direct add: nothing in the product reads the value back except as text.
    /// </summary>
    [Theory]
    [InlineData(
        PilotTransmissionKind.Readback,
        "descend and maintain five thousand",
        "descend and maintain five thousand, november one two three alpha bravo.",
        PilotResponder.SourceResponse
    )]
    [InlineData(PilotTransmissionKind.Proactive, "tower, ready to taxi", "tower, november one two three alpha bravo ready to taxi.", "Proactive")]
    [InlineData(
        PilotTransmissionKind.Report,
        "five-mile final, runway two eight right",
        "five mile final runway two eight right, november one two three alpha bravo.",
        "Report"
    )]
    [InlineData(
        PilotTransmissionKind.SayReadback,
        "traffic in sight",
        "traffic in sight, november one two three alpha bravo.",
        PilotResponder.SourceSayReadback
    )]
    public void LastPilotTransmission_IsRecordedWhenALineIsDequeued_ForEveryKind(
        PilotTransmissionKind kind,
        string terminal,
        string spoken,
        string sourceKind
    )
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState aircraft = NewAircraft("N123AB");
        world.AddAircraft(aircraft);
        QueueLine(aircraft, terminal, spoken, sourceKind, kind);

        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 10, rngSeed: 1);

        PilotTransmission broadcast = Assert.Single(drained);
        AircraftLastPilotTransmission last = Assert.IsType<AircraftLastPilotTransmission>(aircraft.LastPilotTransmission);
        Assert.Equal(terminal, last.Text);
        Assert.Equal(spoken, last.SpeechText);
        Assert.Equal(kind, last.Kind);
        Assert.Equal(sourceKind, last.SourceKind);
        Assert.Equal(FrequencyState.EstimateAirtimeSeconds(spoken), last.CanonicalAirtimeSeconds);
        Assert.Equal(10, last.AtSeconds);
        // The dequeued line carried the same fields; the store is a copy of it, not a second build of the words.
        Assert.Equal(terminal, broadcast.Text);
        Assert.Equal(spoken, broadcast.SpeechText);
        Assert.Equal(sourceKind, broadcast.SourceKind);
    }

    [Fact]
    public void LastPilotTransmission_IsNotRecordedWhileTheLineIsOnlyQueued()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState aircraft = NewAircraft("N123AB");
        world.AddAircraft(aircraft);
        QueueLine(
            aircraft,
            "fly heading 270",
            "fly heading two seven zero, november one two three alpha bravo.",
            PilotResponder.SourceResponse,
            PilotTransmissionKind.Readback
        );

        Assert.Null(aircraft.LastPilotTransmission);
        Assert.Single(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void LastPilotTransmission_RepeatDequeue_ClearsPendingRepeatAndKeepsContent()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState aircraft = NewAircraft("N123AB");
        aircraft.LastPilotTransmission = new AircraftLastPilotTransmission
        {
            Text = "descend and maintain five thousand",
            SpeechText = "descend and maintain five thousand, november one two three alpha bravo.",
            Kind = PilotTransmissionKind.Readback,
            SourceKind = PilotResponder.SourceResponse,
            CanonicalAirtimeSeconds = 4.5,
            AtSeconds = 5,
        };
        aircraft.PendingRepeat = true;
        world.AddAircraft(aircraft);
        aircraft.PendingPilotTransmissions.Add(RepeatOf(aircraft.LastPilotTransmission));

        PilotTransmission repeated = Assert.Single(world.DrainReadyPilotTransmissions(elapsedSeconds: 20, rngSeed: 1));

        Assert.False(aircraft.PendingRepeat);
        AircraftLastPilotTransmission last = Assert.IsType<AircraftLastPilotTransmission>(aircraft.LastPilotTransmission);
        Assert.Equal("descend and maintain five thousand", last.Text);
        Assert.Equal("descend and maintain five thousand, november one two three alpha bravo.", last.SpeechText);
        Assert.Equal(PilotTransmissionKind.Readback, last.Kind);
        Assert.Equal(PilotResponder.SourceResponse, last.SourceKind);
        Assert.Equal(repeated.CanonicalAirtimeSeconds, last.CanonicalAirtimeSeconds);
        Assert.Equal(5, last.AtSeconds);
    }

    [Fact]
    public void LastPilotTransmission_SurvivesASnapshotRoundTrip()
    {
        AircraftState aircraft = NewAircraft("N123AB");
        aircraft.LastPilotTransmission = new AircraftLastPilotTransmission
        {
            Text = "descend and maintain five thousand",
            SpeechText = "descend and maintain five thousand, november one two three alpha bravo.",
            Kind = PilotTransmissionKind.Readback,
            SourceKind = PilotResponder.SourceResponse,
            CanonicalAirtimeSeconds = 4.5,
            AtSeconds = 5,
        };
        aircraft.PendingRepeat = true;

        string json = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        AircraftLastPilotTransmission last = Assert.IsType<AircraftLastPilotTransmission>(restored.LastPilotTransmission);
        Assert.Equal("descend and maintain five thousand", last.Text);
        Assert.Equal("descend and maintain five thousand, november one two three alpha bravo.", last.SpeechText);
        Assert.Equal(PilotTransmissionKind.Readback, last.Kind);
        Assert.Equal(PilotResponder.SourceResponse, last.SourceKind);
        Assert.Equal(4.5, last.CanonicalAirtimeSeconds);
        Assert.Equal(5, last.AtSeconds);
        Assert.True(restored.PendingRepeat);
    }

    [Fact]
    public void LastPilotTransmission_RestoresNullFromASnapshotWithoutTheField()
    {
        AircraftState aircraft = NewAircraft("N123AB");
        aircraft.LastPilotTransmission = new AircraftLastPilotTransmission
        {
            Text = "descend and maintain five thousand",
            SpeechText = "descend and maintain five thousand, november one two three alpha bravo.",
            Kind = PilotTransmissionKind.Readback,
            SourceKind = PilotResponder.SourceResponse,
            CanonicalAirtimeSeconds = 4.5,
            AtSeconds = 5,
        };
        aircraft.PendingRepeat = true;
        JsonObject node = JsonSerializer.SerializeToNode(aircraft.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();

        Assert.True(node.Remove("LastPilotTransmission"));
        Assert.True(node.Remove("PendingRepeat"));

        AircraftSnapshotDto dto = node.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Null(restored.LastPilotTransmission);
        Assert.False(restored.PendingRepeat);
    }

    [Fact]
    public void LastPilotTransmission_IsNotWrittenToJsonWhenNull()
    {
        AircraftState aircraft = NewAircraft("N123AB");

        string json = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);

        Assert.DoesNotContain("LastPilotTransmission", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingRepeat", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LastPilotTransmission_SpeakerRemovedWithALineQueued_StillBroadcastsAndDoesNotThrow()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState aircraft = NewAircraft("N123AB");
        world.AddAircraft(aircraft);
        // The line sits on the frequency, as it does once the drain moves a pilot's pending lines in. The aircraft then
        // leaves before the line's airtime, so the dequeue has nobody to record against.
        world.ActiveFrequency.Enqueue(
            new PilotTransmission(
                "N123AB",
                "tower, ready to taxi",
                "tower, november one two three alpha bravo ready to taxi.",
                PilotResponder.SourceResponse,
                PilotTransmissionKind.Proactive,
                FrequencyState.EstimateAirtimeSeconds("tower, november one two three alpha bravo ready to taxi.")
            )
        );
        world.RemoveAircraft("N123AB");

        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 10, rngSeed: 1);

        PilotTransmission broadcast = Assert.Single(drained);
        Assert.Equal("N123AB", broadcast.Callsign);
        Assert.Null(aircraft.LastPilotTransmission);
    }

    [Fact]
    public void PendingRepeat_IsClearedWhenNobodyAnswersPilots()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState calling = NewAircraft("N123AB");
        AircraftState quiet = NewAircraft("N456AB");
        world.AddAircraft(calling);
        world.AddAircraft(quiet);
        calling.PendingRepeat = true;
        quiet.PendingRepeat = true;
        quiet.LastPilotTransmission = new AircraftLastPilotTransmission
        {
            Text = "tower, ready to taxi",
            SpeechText = "tower, november four five six alpha bravo ready to taxi.",
            Kind = PilotTransmissionKind.Proactive,
            SourceKind = PilotResponder.SourceResponse,
            CanonicalAirtimeSeconds = 3.0,
            AtSeconds = 4,
        };
        QueueLine(
            calling,
            "tower, ready to taxi",
            "tower, november one two three alpha bravo ready to taxi.",
            PilotResponder.SourceResponse,
            PilotTransmissionKind.Proactive
        );

        world.DiscardAllPilotTransmissions();

        Assert.False(calling.PendingRepeat);
        Assert.False(quiet.PendingRepeat);
        Assert.Empty(calling.PendingPilotTransmissions);
        AircraftLastPilotTransmission last = Assert.IsType<AircraftLastPilotTransmission>(quiet.LastPilotTransmission);
        Assert.Equal("tower, ready to taxi", last.Text);
    }

    /// <summary>
    /// Queues one line through the queue path its kind uses: a SAY reply through the readback overload with independent
    /// terminal and spoken forms, every other kind through the direct add so its source kind stays its own.
    /// </summary>
    private static void QueueLine(AircraftState aircraft, string terminal, string spoken, string sourceKind, PilotTransmissionKind kind)
    {
        if (kind is PilotTransmissionKind.SayReadback)
        {
            PilotResponder.QueueSoloPilotReadback(aircraft, new PilotSpeechText(terminal, spoken), sourceKind);
            return;
        }

        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(aircraft.Callsign, terminal, spoken, sourceKind, kind, FrequencyState.EstimateAirtimeSeconds(spoken))
        );
    }

    /// <summary>The copy a "say again" re-queues: the stored words, marked as the repeat.</summary>
    private static PilotTransmission RepeatOf(AircraftLastPilotTransmission last) =>
        new("N123AB", last.Text, last.SpeechText, last.SourceKind, last.Kind, last.CanonicalAirtimeSeconds) { IsRepeat = true };

    private static AircraftState NewAircraft(string callsign) => new() { Callsign = callsign, AircraftType = "C172" };
}

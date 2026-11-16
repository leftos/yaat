using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Where an ERAM entry was typed, carried on the records its handlers write (<see cref="RecordedEramEntry"/>,
/// <see cref="RecordedEramCrrGroup"/>, <see cref="RecordedEramRoomEntry"/>, and — set only when an ERAM entry wrote them —
/// <see cref="RecordedCommand"/>, <see cref="RecordedAmendFlightPlan"/>, <see cref="RecordedRequestNewBeaconCode"/> and
/// <see cref="RecordedChat"/>) so a bug bundle shows who typed it. Provenance only: replay applies a record the same
/// whatever its source. Written by name; a number, as an older build may have written it, still reads. An ERAM record
/// with no source reads as <see cref="Crc"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EramEntrySource>))]
public enum EramEntrySource
{
    /// <summary>A CRC client's ERAM keyboard (<c>ProcessEramMessage</c>).</summary>
    Crc,

    /// <summary>The YAAT Scope's entry preview (<c>TrainingHub.SendScopeEramEntry</c>).</summary>
    Scope,
}

using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Situation;

/// <summary>
/// The aircraft's stored situation and its liftoff time. The <c>Situation</c> spine step classifies the aircraft once
/// a second into <see cref="Current"/>, passing the stored value back as the previous one for the hysteresis bands;
/// the server's training-hub projection reads <see cref="Current"/> rather than classifying again.
/// </summary>
public class AircraftSituationState
{
    /// <summary>The situation the last <c>Situation</c> step computed; <see cref="AircraftSituation.Unknown"/> until it first runs.</summary>
    public AircraftSituation Current { get; set; } = AircraftSituation.Unknown;

    /// <summary>
    /// Sim time (seconds) of the aircraft's most recent liftoff, stamped by the <c>Situation</c> step when it finds the
    /// aircraft airborne after seeing it on the ground (<see cref="WasOnGround"/>), to the second. Null for an aircraft
    /// that has not lifted off since it spawned or was restored from a snapshot written before the field existed: it
    /// reads as departed long ago.
    /// </summary>
    public double? AirborneAtSeconds { get; set; }

    /// <summary>
    /// Whether the aircraft was on the ground at the last <c>Situation</c> step. False until that step first sees it
    /// on the ground, so an aircraft spawned airborne is never stamped; a warp into the air clears it, so a warp is
    /// never a liftoff.
    /// </summary>
    public bool WasOnGround { get; set; }

    /// <summary>
    /// The flags the last <c>Situation</c> step computed (<see cref="SituationFlagCalculator"/>); the next step reads
    /// them back as the previous second's, which the latched flags hold on. <see cref="SituationFlags.None"/> until it
    /// first runs.
    /// </summary>
    public SituationFlags Flags { get; set; }

    public AircraftSituationStateDto ToSnapshot() =>
        new()
        {
            Current = (int)Current,
            AirborneAtSeconds = AirborneAtSeconds,
            WasOnGround = WasOnGround,
            Flags = (int)Flags,
        };

    public static AircraftSituationState FromSnapshot(AircraftSituationStateDto dto) =>
        new()
        {
            Current = (AircraftSituation)dto.Current,
            AirborneAtSeconds = dto.AirborneAtSeconds,
            WasOnGround = dto.WasOnGround,
            Flags = (SituationFlags)dto.Flags,
        };
}

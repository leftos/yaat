using System.Text.Json;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// A room's state at one whole second of its own run, held as Brotli-compressed snapshot JSON so a rewind or seek can
/// start near its target instead of re-ticking from t=0. Captured by the live host at the top of a second, before the
/// second runs, so every action stamped with that second is already in it.
/// </summary>
public sealed class LiveCheckpoint
{
    private LiveCheckpoint(double elapsedSeconds, int actionIndex, byte[] brotliJson)
    {
        ElapsedSeconds = elapsedSeconds;
        ActionIndex = actionIndex;
        BrotliJson = brotliJson;
    }

    /// <summary>The scenario second the state was captured at.</summary>
    public double ElapsedSeconds { get; }

    /// <summary>The index of the last action in the tape when the state was captured, as <see cref="TimedSnapshot.ActionIndex"/>.</summary>
    public int ActionIndex { get; }

    /// <summary>
    /// The captured <see cref="StateSnapshotDto"/> serialized with <see cref="RecordingJsonOptions.Default"/> and
    /// Brotli-compressed.
    /// </summary>
    public byte[] BrotliJson { get; }

    /// <summary>
    /// Compresses <paramref name="state"/> into a checkpoint, the serialization and compression a recording archive uses.
    /// </summary>
    /// <param name="state">The captured state.</param>
    /// <param name="elapsedSeconds">The scenario second it was captured at.</param>
    /// <param name="actionIndex">The index of the last action in the tape at capture.</param>
    /// <returns>The checkpoint.</returns>
    public static LiveCheckpoint Capture(StateSnapshotDto state, double elapsedSeconds, int actionIndex)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, RecordingJsonOptions.Default);
        return new LiveCheckpoint(elapsedSeconds, actionIndex, RecordingCompression.Compress(json));
    }

    /// <summary>Decompresses the checkpoint into the snapshot a rewind restores.</summary>
    /// <returns>The state as a timed snapshot at <see cref="ElapsedSeconds"/>.</returns>
    public TimedSnapshot ToTimedSnapshot()
    {
        string json = RecordingCompression.Decompress(BrotliJson);
        StateSnapshotDto state =
            JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException($"The live checkpoint at t={ElapsedSeconds}s holds no state");
        return new TimedSnapshot
        {
            ElapsedSeconds = ElapsedSeconds,
            ActionIndex = ActionIndex,
            State = state,
        };
    }
}

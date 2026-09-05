namespace Yaat.Sim.Simulation.Spine;

/// <summary>One recorded step: the id and, for <see cref="StepId.Physics"/>, which of the sub-ticks it was; 0 otherwise.</summary>
public readonly record struct TracedStep(StepId Id, int SubTick);

/// <summary>
/// The per-second record of which spine steps ran, in what order, on this engine. On by default and allocation-free
/// once its buffers have grown: one list append and two array increments per step. It exists because the snapshot
/// oracle cannot see ordering — every host iterates the same lists, so what the trace catches is a host that skips a
/// segment, a second opened against the wrong time, a wrong sub-tick count, or the sub-tick replay split drifting
/// from the whole-second path. A host slot that does nothing leaves the trace untouched; that is the oracle's job.
/// </summary>
public sealed class StepTrace
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private static readonly int StepCount = Enum.GetValues<StepId>().Length;

    private readonly List<TracedStep> _sequence = new(64);
    private readonly int[] _inSecond = new int[StepCount];
    private readonly long[] _totals = new long[StepCount];

    /// <summary>The second the trace currently describes; -1 before the first <see cref="OpenSecond"/>.</summary>
    public int LastSecond { get; private set; } = -1;

    /// <summary>FNV-1a 64 over the second and every (id, sub-tick) recorded in it, updated as steps run.</summary>
    public ulong LastDigest { get; private set; } = FnvOffsetBasis;

    /// <summary>The steps recorded since <see cref="OpenSecond"/>, in execution order.</summary>
    public IReadOnlyList<TracedStep> LastSequence => _sequence;

    /// <summary>How many times <paramref name="id"/> ran in the current second.</summary>
    public int CountInLastSecond(StepId id) => _inSecond[(int)id];

    /// <summary>How many times <paramref name="id"/> has run on this engine since construction.</summary>
    public long TotalCount(StepId id) => _totals[(int)id];

    internal void OpenSecond(int second)
    {
        _sequence.Clear();
        Array.Clear(_inSecond);
        LastSecond = second;
        LastDigest = FnvOffsetBasis;
        Mix(unchecked((ulong)second));
    }

    internal void Record(StepId id, int subTick)
    {
        _sequence.Add(new TracedStep(id, subTick));
        _inSecond[(int)id]++;
        _totals[(int)id]++;
        Mix((ulong)id);
        Mix(unchecked((ulong)subTick));
    }

    private void Mix(ulong value)
    {
        ulong digest = LastDigest;
        for (int shift = 0; shift < 64; shift += 8)
        {
            digest = (digest ^ ((value >> shift) & 0xFF)) * FnvPrime;
        }

        LastDigest = digest;
    }
}

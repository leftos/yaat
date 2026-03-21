namespace Yaat.Sim;

/// <summary>
/// Xoshiro256** PRNG with serializable state. Drop-in replacement for <see cref="Random"/>
/// that exposes its internal state for snapshot capture/restore.
/// </summary>
public sealed class SerializableRandom : Random
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public SerializableRandom(int seed)
    {
        // Seed the state using SplitMix64 (same approach as reference Xoshiro256**)
        ulong s = (ulong)seed;
        _s0 = SplitMix64(ref s);
        _s1 = SplitMix64(ref s);
        _s2 = SplitMix64(ref s);
        _s3 = SplitMix64(ref s);
    }

    public SerializableRandom(long s0, long s1, long s2, long s3)
    {
        _s0 = (ulong)s0;
        _s1 = (ulong)s1;
        _s2 = (ulong)s2;
        _s3 = (ulong)s3;
    }

    public RngState GetState() => new((long)_s0, (long)_s1, (long)_s2, (long)_s3);

    public override int Next()
    {
        return (int)(NextUInt64() >> 33); // 31 non-negative bits
    }

    public override int Next(int maxValue)
    {
        if (maxValue <= 0)
        {
            if (maxValue == 0)
            {
                return 0;
            }
            throw new ArgumentOutOfRangeException(nameof(maxValue), "maxValue must be non-negative.");
        }

        // Debiased integer multiplication (Lemire's method)
        ulong m = (ulong)(uint)maxValue;
        ulong x = NextUInt64() >> 32;
        ulong prod = x * m;
        uint leftover = (uint)prod;
        if (leftover < m)
        {
            ulong threshold = (uint)(-(int)m) % m;
            while (leftover < threshold)
            {
                x = NextUInt64() >> 32;
                prod = x * m;
                leftover = (uint)prod;
            }
        }

        return (int)(prod >> 32);
    }

    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minValue), "minValue must be less than or equal to maxValue.");
        }

        long range = (long)maxValue - minValue;
        if (range <= int.MaxValue)
        {
            return Next((int)range) + minValue;
        }

        // Large range: use 64-bit path
        return (int)((long)(NextUInt64() % (ulong)range) + minValue);
    }

    public override double NextDouble()
    {
        // 53 bits of mantissa
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    protected override double Sample()
    {
        return NextDouble();
    }

    private ulong NextUInt64()
    {
        // Xoshiro256** algorithm
        ulong result = RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;

        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);

        return result;
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

    private static ulong SplitMix64(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// Serializable snapshot of <see cref="SerializableRandom"/> internal state.
/// </summary>
public record RngState(long S0, long S1, long S2, long S3);

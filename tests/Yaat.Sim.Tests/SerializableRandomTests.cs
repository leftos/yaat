using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

public class SerializableRandomTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var rng1 = new SerializableRandom(42);
        var rng2 = new SerializableRandom(42);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(rng1.Next(), rng2.Next());
        }
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var rng1 = new SerializableRandom(42);
        var rng2 = new SerializableRandom(99);

        var seq1 = Enumerable.Range(0, 20).Select(_ => rng1.Next()).ToList();
        var seq2 = Enumerable.Range(0, 20).Select(_ => rng2.Next()).ToList();

        Assert.False(seq1.SequenceEqual(seq2));
    }

    [Fact]
    public void Next_ReturnsNonNegative()
    {
        var rng = new SerializableRandom(42);
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(rng.Next() >= 0);
        }
    }

    [Fact]
    public void NextMaxValue_RespectsBound()
    {
        var rng = new SerializableRandom(42);
        for (int i = 0; i < 1000; i++)
        {
            int val = rng.Next(10);
            Assert.InRange(val, 0, 9);
        }
    }

    [Fact]
    public void NextMinMaxValue_RespectsBounds()
    {
        var rng = new SerializableRandom(42);
        for (int i = 0; i < 1000; i++)
        {
            int val = rng.Next(5, 15);
            Assert.InRange(val, 5, 14);
        }
    }

    [Fact]
    public void NextDouble_ReturnsBetweenZeroAndOne()
    {
        var rng = new SerializableRandom(42);
        for (int i = 0; i < 1000; i++)
        {
            double val = rng.NextDouble();
            Assert.InRange(val, 0.0, 1.0);
            Assert.NotEqual(1.0, val);
        }
    }

    [Fact]
    public void GetState_CapturesCurrentPosition()
    {
        var rng = new SerializableRandom(42);

        // Advance the RNG
        for (int i = 0; i < 50; i++)
        {
            rng.Next();
        }

        var state = rng.GetState();

        // Create a new RNG from captured state
        var restored = new SerializableRandom(state.S0, state.S1, state.S2, state.S3);

        // Both should produce identical sequences from this point
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(rng.Next(), restored.Next());
        }
    }

    [Fact]
    public void GetState_RestoresNextDouble()
    {
        var rng = new SerializableRandom(123);

        for (int i = 0; i < 30; i++)
        {
            rng.NextDouble();
        }

        var state = rng.GetState();
        var restored = new SerializableRandom(state.S0, state.S1, state.S2, state.S3);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(rng.NextDouble(), restored.NextDouble());
        }
    }

    [Fact]
    public void GetState_RestoresNextWithBounds()
    {
        var rng = new SerializableRandom(77);

        for (int i = 0; i < 25; i++)
        {
            rng.Next(0, 8);
        }

        var state = rng.GetState();
        var restored = new SerializableRandom(state.S0, state.S1, state.S2, state.S3);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(rng.Next(0, 8), restored.Next(0, 8));
        }
    }

    [Fact]
    public void NextMaxZero_ReturnsZero()
    {
        var rng = new SerializableRandom(42);
        Assert.Equal(0, rng.Next(0));
    }

    [Fact]
    public void NextSameMinMax_ReturnsMin()
    {
        var rng = new SerializableRandom(42);
        Assert.Equal(5, rng.Next(5, 5));
    }

    [Fact]
    public void BeaconCodeGeneration_DeterministicWithState()
    {
        var rng = new SerializableRandom(42);

        // Generate some beacon codes to advance state
        for (int i = 0; i < 10; i++)
        {
            SimulationWorld.GenerateBeaconCode(rng);
        }

        var state = rng.GetState();
        var restored = new SerializableRandom(state.S0, state.S1, state.S2, state.S3);

        // Beacon codes from this point should match
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(SimulationWorld.GenerateBeaconCode(rng), SimulationWorld.GenerateBeaconCode(restored));
        }
    }

    [Fact]
    public void Distribution_CoversFullRange()
    {
        var rng = new SerializableRandom(42);
        var buckets = new int[10];

        for (int i = 0; i < 10000; i++)
        {
            buckets[rng.Next(10)]++;
        }

        // Each bucket should have roughly 1000 hits (allow wide tolerance)
        foreach (int count in buckets)
        {
            Assert.InRange(count, 700, 1300);
        }
    }
}

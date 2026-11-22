using Xunit;
using Yaat.Sim.Authoring;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioIdGenerator"/> makes ULIDs: 26 Crockford base32 characters, a millisecond timestamp then random
/// bits from the caller's <see cref="Random"/>, so a seed and a clock reproduce the id.
/// </summary>
public class ScenarioIdGeneratorTests
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 30, 15, 123, TimeSpan.Zero);

    [Fact]
    public void Create_Is26CrockfordChars_AndAValidStoreId()
    {
        string id = ScenarioIdGenerator.Create(Now, new Random(7));

        Assert.Equal(26, id.Length);
        Assert.All(id, c => Assert.Contains(c, Crockford));
        Assert.True(LocalScenarioStore.IsValidId(id));
    }

    [Fact]
    public void Create_SameClockAndSeed_GivesTheSameId() =>
        Assert.Equal(ScenarioIdGenerator.Create(Now, new Random(42)), ScenarioIdGenerator.Create(Now, new Random(42)));

    [Fact]
    public void Create_SameClockOtherSeed_SharesOnlyTheTimestamp()
    {
        string a = ScenarioIdGenerator.Create(Now, new Random(1));
        string b = ScenarioIdGenerator.Create(Now, new Random(2));

        Assert.Equal(a[..10], b[..10]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Create_ConsecutiveMilliseconds_SortAscending()
    {
        var random = new Random(3);
        string[] ids = [.. Enumerable.Range(0, 50).Select(i => ScenarioIdGenerator.Create(Now.AddMilliseconds(i), random))];

        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public void Create_EncodesTheTimestampLikeVnas()
    {
        // The committed vNAS scenario 01H06NVK7VN8BS7MCDXHKJZ7MQ: its first ten characters are its creation time.
        var created = DateTimeOffset.FromUnixTimeMilliseconds(DecodeTimestamp("01H06NVK7V"));

        Assert.Equal(new DateTimeOffset(2023, 5, 12, 0, 30, 31, 163, TimeSpan.Zero), created);
        Assert.Equal("01H06NVK7V", ScenarioIdGenerator.Create(created, new Random(0))[..10]);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "0000000000000000")]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "ZZZZZZZZZZZZZZZZ")]
    [InlineData(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0xDC }, "04HMASW9NF6YZZPW")]
    public void Create_EncodesThe80RandomBitsMostSignificantFirst(byte[] bytes, string expected) =>
        Assert.Equal(expected, ScenarioIdGenerator.Create(Now, new FixedBytes(bytes))[10..]);

    [Fact]
    public void Create_Before1970_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScenarioIdGenerator.Create(DateTimeOffset.UnixEpoch.AddMilliseconds(-1), new Random(0)));

    [Fact]
    public void Create_NullRandom_Throws() => Assert.Throws<ArgumentNullException>(() => ScenarioIdGenerator.Create(Now, null!));

    private sealed class FixedBytes(byte[] bytes) : Random
    {
        public override void NextBytes(Span<byte> buffer) => bytes.CopyTo(buffer);
    }

    private static long DecodeTimestamp(string prefix) =>
        prefix.Aggregate(0L, (acc, c) => (acc * 32) + Crockford.IndexOf(c, StringComparison.Ordinal));
}

using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

public sealed class NaturalCommandNormalizerTests
{
    private readonly PhraseologyCommandMapper _ruleMapper = new();

    [Fact]
    public async Task TryNormalizeAsync_RuleMappedSoloPhrase_ReturnsDispatchableCanonical()
    {
        var context = new SpeechContext(["SWA123"], [], string.Empty);

        var result = await NaturalCommandNormalizer.TryNormalizeAsync(
            "southwest one two three descend and maintain five thousand",
            context,
            _ruleMapper,
            llmMapper: null,
            callsignResolver: null,
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal("SWA123", result.Callsign);
        Assert.Equal("DM 5000", result.CanonicalCommand);
        Assert.Equal("SWA123 DM 5000", result.CommandText);
        Assert.NotNull(CommandSchemeParser.ParseCompound(result.CanonicalCommand, CommandScheme.Default()));
    }

    [Fact]
    public async Task TryNormalizeAsync_UnmappedPhraseWithoutCallsign_ReturnsNull()
    {
        var context = new SpeechContext(["SWA123"], [], string.Empty);

        var result = await NaturalCommandNormalizer.TryNormalizeAsync(
            "hello there",
            context,
            _ruleMapper,
            llmMapper: null,
            callsignResolver: null,
            CancellationToken.None
        );

        Assert.Null(result);
    }
}

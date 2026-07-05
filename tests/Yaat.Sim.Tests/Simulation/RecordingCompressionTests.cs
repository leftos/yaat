using System.Text;
using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="RecordingCompression.Decompress"/> autodetects gzip / Brotli / plain JSON. Brotli has no
/// magic number and its first byte can be '{' or '[', so detection decodes as Brotli first and only
/// treats the input as plain JSON when Brotli can't read it — the old first-byte "looks like JSON"
/// heuristic misfired on a Brotli stream that happened to start with those bytes (issue #264, real
/// <c>oak-taxi-recording.br</c> starts with '[').
/// </summary>
public class RecordingCompressionTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"Version\":2,\"Snapshots\":[{\"SchemaVersion\":1}]}")]
    [InlineData("[1,2,3,4,5,6,7,8,9,10]")]
    [InlineData("plain non-json text with no brackets")]
    public void Decompress_RoundTripsBrotli(string content)
    {
        var compressed = RecordingCompression.Compress(Encoding.UTF8.GetBytes(content));
        Assert.Equal(content, RecordingCompression.Decompress(compressed));
    }

    [Fact]
    public void Decompress_PlainJsonStartingWithBrace_ReturnedAsIs()
    {
        // Plain JSON is not a valid Brotli stream, so it falls through to the UTF-8 path even though it
        // starts with '{'. (This is the branch that must keep legacy .json recordings loadable.)
        var bytes = Encoding.UTF8.GetBytes("{\"Version\":1,\"ScenarioJson\":\"{}\"}");
        Assert.Equal("{\"Version\":1,\"ScenarioJson\":\"{}\"}", RecordingCompression.Decompress(bytes));
    }

    [Fact]
    public void Decompress_PlainJsonArray_ReturnedAsIs()
    {
        var bytes = Encoding.UTF8.GetBytes("[{\"a\":1},{\"b\":2}]");
        Assert.Equal("[{\"a\":1},{\"b\":2}]", RecordingCompression.Decompress(bytes));
    }
}

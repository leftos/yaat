using System.Text.Json;
using Xunit;
using Yaat.Client.Tdls.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The browser vTDLS client's hub connection resolves payload types through <see cref="YaatTdlsHubJsonContext"/> alone,
/// with no reflection fallback. A server call whose argument type the context does not register fails to bind and is
/// dropped without an error, so every payload the browser listens for has to resolve through the context by itself.
/// </summary>
public class TdlsHubJsonContextTests
{
    private static readonly JsonSerializerOptions ContextOnly = new() { TypeInfoResolver = YaatTdlsHubJsonContext.Default };

    /// <summary>The server's ScenarioLoaded and ScenarioUnloaded payloads, whose content the browser ignores.</summary>
    [Theory]
    [InlineData("""{"artccId":"ZOA"}""")]
    [InlineData("{}")]
    public void ScenarioChangedPayload_DeserializesThroughTheTdlsContextAlone(string json)
    {
        object? payload = JsonSerializer.Deserialize(json, typeof(BrowserTdlsScenarioChangedDto), ContextOnly);

        Assert.IsType<BrowserTdlsScenarioChangedDto>(payload);
    }

    /// <summary>
    /// A sent item's payload carries the SID and transition names resolved at send time beside their ids, camelCased the
    /// way the server's SignalR JSON writes them. The hub matches property names case-insensitively, as SignalR's own
    /// payload options do, with the context still the only resolver.
    /// </summary>
    [Fact]
    public void SentClearance_DeserializesTheSidAndTransitionNames()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = YaatTdlsHubJsonContext.Default, PropertyNameCaseInsensitive = true };
        const string json = """{"sid":"cfg-west-cndel","transition":"t-sns","sidName":"CNDEL5","transitionName":"SNS"}""";

        var clearance = (ClearanceDto?)JsonSerializer.Deserialize(json, typeof(ClearanceDto), options);

        Assert.NotNull(clearance);
        Assert.Equal("cfg-west-cndel", clearance.Sid);
        Assert.Equal("CNDEL5", clearance.SidName);
        Assert.Equal("SNS", clearance.TransitionName);
    }
}

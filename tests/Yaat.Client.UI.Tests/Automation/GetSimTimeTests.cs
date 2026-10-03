using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <c>get_sim_time</c> over a real named pipe: the sim clock, pause state and rate the client's state reports, and the coded
/// error while the main window is not up.
/// </summary>
public sealed class GetSimTimeTests : AutomationHostFixture
{
    private async Task<JsonElement> GetSimTime(Func<IAutomationState?> state)
    {
        using AutomationHost host = StartHost(() => Windows, state);
        await using AutomationPipeTestClient client = await Connect();
        return await Send(client, ProtocolMethods.GetSimTime, new { });
    }

    [AvaloniaFact]
    public async Task GetSimTime_ReportsTheStateClock()
    {
        var state = new ClockState(754.5, true, 4);

        JsonElement result = Result(await GetSimTime(() => state));

        Assert.Equal(754.5, result.GetProperty("simSeconds").GetDouble());
        Assert.True(result.GetProperty("isPaused").GetBoolean());
        Assert.Equal(4, result.GetProperty("simRate").GetInt32());
    }

    [AvaloniaFact]
    public async Task GetSimTime_NoMainWindow_AnswersTheNoStateError()
    {
        JsonElement error = Error(await GetSimTime(() => null), AutomationErrorCodes.UnsupportedOperation);

        Assert.Contains("main window", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>An <see cref="IAutomationState"/> whose clock reads fixed values; nothing else in it is read.</summary>
    private sealed class ClockState(double simSeconds, bool isPaused, int simRate) : IAutomationState
    {
        public IReadOnlyList<AircraftModel> Aircraft => [];

        public double ScenarioElapsedSeconds => simSeconds;

        public bool IsPaused => isPaused;

        public int SimRate => simRate;

        public long TerminalCursor => 0;

        public IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor) => [];

        public Task<AutomationActionOutcome> PauseAsync() => throw new InvalidOperationException("get_sim_time never pauses.");

        public Task<AutomationActionOutcome> SetRateAsync(int rate) => throw new InvalidOperationException("get_sim_time never sets the rate.");
    }
}

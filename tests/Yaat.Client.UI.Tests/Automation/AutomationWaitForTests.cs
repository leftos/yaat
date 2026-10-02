using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <c>wait_for</c> over a real named pipe: each condition met at once or across a delayed UI change, the timeout and its
/// clamp, the coded param errors, and the poll stopping when the host stops or the client goes.
/// </summary>
public sealed class AutomationWaitForTests : AutomationHostFixture
{
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PollDeadline = TimeSpan.FromSeconds(5);

    /// <summary>A border whose <see cref="Probe"/> getter counts its reads, so a test sees each UI-thread poll of <c>[Probe=...]</c>.</summary>
    private sealed class ProbeBorder : Border
    {
        private int _reads;

        public int Reads => _reads;

        public string Probe
        {
            get
            {
                _reads++;
                return "idle";
            }
        }
    }

    private async Task<JsonElement> WaitFor(object parameters)
    {
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        return await Send(client, ProtocolMethods.WaitFor, parameters);
    }

    /// <summary>
    /// Connects, checks the condition does not hold yet, arms <paramref name="change"/> to run after <see cref="ChangeDelay"/>,
    /// then sends <c>wait_for</c>, and checks the answer came after the change.
    /// </summary>
    private async Task<JsonElement> WaitForAcross(Func<bool> conditionHolds, Action change, object parameters)
    {
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        Assert.False(conditionHolds(), "The condition already holds before the change.");
        bool changed = false;
        DispatcherTimer.RunOnce(
            () =>
            {
                change();
                changed = true;
            },
            ChangeDelay
        );

        JsonElement response = await Send(client, ProtocolMethods.WaitFor, parameters);

        Assert.True(changed, $"wait_for answered before the change: {response}");
        return response;
    }

    private static string NeverMetRequest() =>
        JsonSerializer.Serialize(
            new
            {
                id = "wait",
                method = ProtocolMethods.WaitFor,
                @params = new
                {
                    selector = "ProbeBorder[Probe=ready]",
                    condition = "exists",
                    timeoutMs = 30000,
                },
            }
        );

    private static async Task UntilPolled(ProbeBorder probe)
    {
        var stopwatch = Stopwatch.StartNew();
        while (probe.Reads == 0)
        {
            Assert.True(stopwatch.Elapsed < PollDeadline, "wait_for never polled the probe.");
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task WaitFor_Exists_SucceedsWithTheMatchCount()
    {
        ShowWindow("WaitWindow", Pad(), null);

        JsonElement result = Result(await WaitFor(new { selector = "#Pad", condition = "exists" }));

        Assert.Equal(1, result.GetProperty("matchCount").GetInt32());
        Assert.True(result.GetProperty("elapsedMs").GetInt32() < 1000);
    }

    [AvaloniaFact]
    public async Task WaitFor_NotExists_SucceedsAfterTheElementIsRemoved()
    {
        var panel = new StackPanel();
        panel.Children.Add(Pad());
        ShowWindow("WaitWindow", panel, null);

        JsonElement result = Result(
            await WaitForAcross(() => panel.Children.Count == 0, () => panel.Children.Clear(), new { selector = "#Pad", condition = "not_exists" })
        );

        Assert.Equal(0, result.GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_Visible_SucceedsAfterTheElementIsShown()
    {
        Border pad = Pad();
        pad.IsVisible = false;
        ShowWindow("WaitWindow", pad, null);

        JsonElement result = Result(
            await WaitForAcross(() => pad.IsVisible, () => pad.IsVisible = true, new { selector = "#Pad", condition = "visible" })
        );

        Assert.Equal(1, result.GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_Enabled_SucceedsAfterTheElementIsEnabled()
    {
        var box = new TextBox { Name = "Box", IsEnabled = false };
        ShowWindow("WaitWindow", box, null);

        JsonElement result = Result(
            await WaitForAcross(() => box.IsEffectivelyEnabled, () => box.IsEnabled = true, new { selector = "#Box", condition = "enabled" })
        );

        Assert.Equal(1, result.GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_TextEquals_SucceedsAfterADelayedTextChange()
    {
        var status = new TextBlock { Name = "Status", Text = "loading" };
        ShowWindow("WaitWindow", status, null);

        JsonElement result = Result(
            await WaitForAcross(
                () => status.Text == "ready",
                () => status.Text = "ready",
                new
                {
                    selector = "#Status",
                    condition = "text_equals",
                    text = "ready",
                }
            )
        );

        Assert.Equal(1, result.GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_TextContains_SucceedsOnAPartialText()
    {
        ShowWindow("WaitWindow", new TextBlock { Name = "Status", Text = "Status: ready" }, null);

        JsonElement result = Result(
            await WaitFor(
                new
                {
                    selector = "#Status",
                    condition = "text_contains",
                    text = "ready",
                }
            )
        );

        Assert.Equal(1, result.GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_CountEquals_SucceedsWhenTheCountIsReached()
    {
        var list = new StackPanel { Name = "List" };
        list.Children.Add(new TextBlock { Text = "one" });
        list.Children.Add(new TextBlock { Text = "two" });
        ShowWindow("WaitWindow", list, null);

        JsonElement result = Result(
            await WaitForAcross(
                () => list.Children.Count == 3,
                () => list.Children.Add(new TextBlock { Text = "three" }),
                new
                {
                    selector = "#List > TextBlock",
                    condition = "count_equals",
                    count = 3,
                }
            )
        );

        Assert.Equal(3, result.GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_ConditionNeverMet_ReturnsTimeoutAfterTheTimeout()
    {
        ShowWindow("WaitWindow", Pad(), null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        var stopwatch = Stopwatch.StartNew();

        JsonElement error = Error(
            await Send(
                client,
                ProtocolMethods.WaitFor,
                new
                {
                    selector = "#Pad",
                    condition = "not_exists",
                    timeoutMs = 500,
                }
            ),
            AutomationErrorCodes.Timeout
        );

        stopwatch.Stop();
        Assert.InRange(stopwatch.ElapsedMilliseconds, 500, 1499);
        Assert.Contains("matched 1", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, error.GetProperty("details").GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_TimeoutBelowTheFloor_IsClampedTo100Ms()
    {
        ShowWindow("WaitWindow", Pad(), null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        var stopwatch = Stopwatch.StartNew();

        JsonElement error = Error(
            await Send(
                client,
                ProtocolMethods.WaitFor,
                new
                {
                    selector = "#Missing",
                    condition = "exists",
                    timeoutMs = 10,
                }
            ),
            AutomationErrorCodes.Timeout
        );

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds >= 100, $"Gave up after {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(100, error.GetProperty("details").GetProperty("timeoutMs").GetInt32());
        Assert.Equal(0, error.GetProperty("details").GetProperty("matchCount").GetInt32());
    }

    [AvaloniaFact]
    public async Task WaitFor_HostDisposedMidWait_StopsPolling()
    {
        var probe = new ProbeBorder();
        ShowWindow("WaitWindow", probe, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        await client.WriteLineAsync(NeverMetRequest());
        await UntilPolled(probe);

        host.Dispose();

        // The connection closes, unanswered, only once the request has unwound, so no poll can be queued after it.
        Assert.Null(await client.ReadLineAsync());
        int readsAtClose = probe.Reads;
        await Task.Delay(400);
        Assert.Equal(readsAtClose, probe.Reads);
    }

    [AvaloniaFact]
    public async Task WaitFor_ClientDisconnectedMidWait_StopsPolling()
    {
        var probe = new ProbeBorder();
        ShowWindow("WaitWindow", probe, null);
        using AutomationHost host = StartHost(() => Windows);
        AutomationPipeTestClient client = await Connect();
        await client.WriteLineAsync(NeverMetRequest());
        await UntilPolled(probe);

        await client.DisposeAsync();

        // The host sees the end of the stream at once; a live poll would read the probe about every 100 ms.
        await Task.Delay(300);
        int readsAfterDisconnect = probe.Reads;
        await Task.Delay(400);
        Assert.Equal(readsAfterDisconnect, probe.Reads);
    }

    [AvaloniaTheory]
    [InlineData("{\"selector\":\"#Pad\",\"condition\":\"appears\"}", "condition")]
    [InlineData("{\"selector\":\"#Pad\"}", "condition")]
    [InlineData("{\"selector\":\"#Pad\",\"condition\":\"text_equals\"}", "text")]
    [InlineData("{\"selector\":\"#Pad\",\"condition\":\"text_contains\",\"text\":5}", "text")]
    [InlineData("{\"selector\":\"#Pad\",\"condition\":\"count_equals\"}", "count")]
    [InlineData("{\"selector\":\"#Pad\",\"condition\":\"count_equals\",\"count\":\"3\"}", "count")]
    [InlineData("{\"selector\":\"#Pad\",\"condition\":\"exists\",\"timeoutMs\":\"fast\"}", "timeoutMs")]
    public async Task WaitFor_BadParams_ReturnInvalidParamNamingTheParam(string paramsJson, string param)
    {
        ShowWindow("WaitWindow", Pad(), null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await SendRawParams(client, ProtocolMethods.WaitFor, paramsJson), AutomationErrorCodes.InvalidParam);

        Assert.Equal(param, error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task WaitFor_BadSelector_ReturnsInvalidSelector()
    {
        ShowWindow("WaitWindow", Pad(), null);

        Error(await WaitFor(new { selector = "Button[", condition = "exists" }), AutomationErrorCodes.InvalidSelector);
    }
}

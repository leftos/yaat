using System.Diagnostics;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class CommandStatusResolverTests
{
    [Fact]
    public void Resolve_SuccessWithNoMessage_ReturnsEmpty()
    {
        var result = new CommandResultDto(Success: true, Message: null);

        var status = CommandStatusResolver.Resolve(result, "N427MX");

        Assert.Equal(string.Empty, status);
    }

    [Fact]
    public void Resolve_SuccessWithMessage_ReturnsEmpty()
    {
        // Helper unconditionally clears on success — even a non-empty server message
        // is dropped, matching the user-confirmed design choice.
        var result = new CommandResultDto(Success: true, Message: "Spawned KOAK A320");

        var status = CommandStatusResolver.Resolve(result, "ADD");

        Assert.Equal(string.Empty, status);
    }

    [Fact]
    public void Resolve_FailureWithMessage_ReturnsMessage()
    {
        var result = new CommandResultDto(Success: false, Message: "Unable, no arrival airport assigned");

        var status = CommandStatusResolver.Resolve(result, "N427MX");

        Assert.Equal("Unable, no arrival airport assigned", status);
    }

    [Fact]
    public void Resolve_FailureWithEmptyMessage_ReturnsDiagnostic()
    {
        // Suppress Debug.Fail so the test runner doesn't break on the diagnostic
        // assertion the helper raises for this server-bug case.
        Trace.Listeners.Clear();
        var result = new CommandResultDto(Success: false, Message: null);

        var status = CommandStatusResolver.Resolve(result, "N427MX");

        Assert.Contains("no reason supplied", status);
        Assert.Contains("N427MX", status);
    }
}

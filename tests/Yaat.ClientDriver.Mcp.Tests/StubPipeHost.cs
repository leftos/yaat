using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// A scripted stand-in for a YAAT client's automation host, for answers a real host in this process cannot be made to give: a
/// real PNG (headless drawing encodes none a decoder reads) or an answer that comes late. It serves one connection.
/// </summary>
internal static class StubPipeHost
{
    /// <summary>Writes the discovery file that names <paramref name="pipeName"/> as this process's automation pipe.</summary>
    public static async Task AdvertiseAsync(string discoveryDirectory, string pipeName, CancellationToken ct)
    {
        Directory.CreateDirectory(discoveryDirectory);
        var discovery = new DiscoveryInfo
        {
            Pid = Environment.ProcessId,
            PipeName = pipeName,
            ProcessName = Process.GetCurrentProcess().ProcessName,
            StartTime = DateTimeOffset.Now,
            ProtocolVersion = ProtocolVersion.Current,
        };
        await File.WriteAllTextAsync(Path.Combine(discoveryDirectory, $"{Environment.ProcessId}.json"), ProtocolSerializer.Serialize(discovery), ct);
    }

    /// <summary>
    /// Answers a ping with this process's pid at once, and every other request with <paramref name="answer"/>'s result for its
    /// method and params after <paramref name="delay"/>, until the client hangs up. A client that hangs up while an answer is
    /// pending ends the serve quietly.
    /// </summary>
    public static async Task ServeAsync(string pipeName, Func<string, JsonElement?, object> answer, TimeSpan delay, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct);
        try
        {
            await ServeConnectionAsync(pipe, answer, delay, ct);
        }
        catch (IOException)
        {
            // The client hung up: the next read, the late answer's write or the writer's closing flush hit the broken pipe.
        }
    }

    private static async Task ServeConnectionAsync(Stream pipe, Func<string, JsonElement?, object> answer, TimeSpan delay, CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, utf8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            using var request = JsonDocument.Parse(line);
            string id = request.RootElement.GetProperty("id").GetString()!;
            string method = request.RootElement.GetProperty("method").GetString()!;
            JsonElement? parameters = request.RootElement.TryGetProperty("params", out JsonElement value) ? value.Clone() : null;
            object result =
                (method == ProtocolMethods.Ping) ? new PingResult(Environment.ProcessId, ProtocolVersion.Current) : answer(method, parameters);
            if (method != ProtocolMethods.Ping)
            {
                await Task.Delay(delay, ct);
            }

            var response = AutomationResponse.Success(id, ProtocolSerializer.ToElement(result));
            await writer.WriteLineAsync(ProtocolSerializer.Serialize(response).AsMemory(), ct);
        }
    }
}

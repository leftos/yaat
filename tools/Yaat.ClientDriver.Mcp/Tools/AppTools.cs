using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// The YAAT client's own app tools, over its automation pipe: client actions it marks for automation (sim rate, radar framing,
/// video maps, solo mode, loading a recording), listed with their parameters and called by name. Each goes to the pid given, or
/// else to the client the last pipe-routed call reached.
/// </summary>
/// <param name="pipes">Finds and caches the YAAT clients' automation pipes, and remembers the client last reached over one.</param>
[McpServerToolType]
public sealed class AppTools(PipeDirectory pipes)
{
    /// <summary>
    /// How long a <c>call_app_tool</c> answer may take: a tool such as <c>load_recording</c> answers only once the room has loaded
    /// the recording and the client has applied it.
    /// </summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(120);

    [McpServerTool]
    [Description(
        "Lists a YAAT client's app tools: client actions it offers by name (e.g. connect, create_room, load_recording, prepare_take, "
            + "get_framing, play, set_sim_rate, center_radar, set_video_map), one line each with its parameters (name: type — meaning) and "
            + "whether it can run now, or why not. Call "
            + "one with call_app_tool. Goes over the client's automation pipe to pid, or with pid 0 to the client the last pipe call "
            + "reached; the first line ends with (pipe)."
    )]
    public async Task<string> ListAppToolsAsync(
        CancellationToken cancellationToken,
        [Description("The YAAT client's process id, or 0 for the client the last pipe call reached.")] int pid = 0
    )
    {
        int target = PipeTools.TargetPid(pipes, pid);
        List<AppToolInfo> tools = await PipeCalls
            .SendForPidAsync<List<AppToolInfo>>(pipes, target, ProtocolMethods.ListAppTools, null, PipeClient.RequestTimeout, cancellationToken)
            .ConfigureAwait(false);
        return FormatList(tools);
    }

    [McpServerTool]
    [Description(
        "Calls one of a YAAT client's app tools (list_app_tools lists them) and waits for it to finish. arguments is a JSON object "
            + "with every one of the tool's parameters by name, e.g. {\"rate\": 4} or {\"callsign\": \"UAL1\", \"rangeNm\": 20}. An unknown "
            + "tool, a missing or unknown argument, or a value of the wrong JSON kind fails with INVALID_PARAM naming it; a tool that "
            + "cannot run now answers with its reason instead of running. Goes over the client's automation pipe to pid, or with pid 0 "
            + "to the client the last pipe call reached; the result ends with (pipe)."
    )]
    public async Task<string> CallAppToolAsync(
        [Description("The app tool's name, e.g. set_sim_rate.")] string tool,
        [Description("A JSON object of the tool's arguments by parameter name; {} for none.")] JsonElement arguments,
        CancellationToken cancellationToken,
        [Description("The YAAT client's process id, or 0 for the client the last pipe call reached.")] int pid = 0
    )
    {
        int target = PipeTools.TargetPid(pipes, pid);
        CallAppToolResult result = await PipeCalls
            .SendForPidAsync<CallAppToolResult>(
                pipes,
                target,
                ProtocolMethods.CallAppTool,
                new CallAppToolParams(tool, arguments),
                CallTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        return result.Available ? $"{tool}: {result.Message} (pipe)" : $"{tool} unavailable: {result.Reason} (pipe)";
    }

    private static string FormatList(IReadOnlyList<AppToolInfo> tools)
    {
        var text = new StringBuilder($"{tools.Count} app tools (pipe)");
        foreach (AppToolInfo tool in tools)
        {
            string parameters = string.Join(
                "; ",
                tool.Parameters.Select(parameter => $"{parameter.Name}: {parameter.Type} — {parameter.Description}")
            );
            string availability = tool.Available ? "available" : $"unavailable: {tool.Reason}";
            text.Append($"{Environment.NewLine}- {tool.Name}({parameters}): {tool.Description} [{availability}]");
        }

        return text.ToString();
    }
}

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>
/// Collects the client errors every pipe answer of one tool call carries, so the call's result can show them. The collector
/// is ambient to the tool call: <see cref="Filter"/> begins one around each call, and <see cref="PipeClient"/> adds each
/// answer's errors, and how many that answer left out, to whichever collector is active in its async flow.
/// </summary>
public static class PipeCallErrors
{
    private static readonly AsyncLocal<Collector?> Active = new();

    /// <summary>
    /// What one tool call's pipe answers have collected so far: the entries to show, and how many further errors the answers
    /// counted but did not list.
    /// </summary>
    public sealed class Collector
    {
        /// <summary>The entries the answers carried, in the order they arrived.</summary>
        public List<ClientLogEntry> Entries { get; } = [];

        /// <summary>How many further errors the answers counted without listing: past the answer's cap, or dropped by the log's ring.</summary>
        public int Omitted { get; internal set; }
    }

    /// <summary>Starts a fresh collector for the current async flow and the calls it makes, and returns it.</summary>
    public static Collector Begin()
    {
        var collector = new Collector();
        Active.Value = collector;
        return collector;
    }

    /// <summary>
    /// Adds one answer's <paramref name="entries"/> and its <paramref name="omitted"/> count to the active collector; does
    /// nothing when no tool call began one.
    /// </summary>
    public static void Add(IEnumerable<ClientLogEntry> entries, int omitted)
    {
        Collector? collector = Active.Value;
        if (collector is null)
        {
            return;
        }

        lock (collector.Entries)
        {
            collector.Entries.AddRange(entries);
            collector.Omitted += omitted;
        }
    }

    /// <summary>
    /// The block a tool result shows: a count line naming every error the call logged, with how many are not shown, then one
    /// line per listed entry with its exception when it has one.
    /// </summary>
    public static string Format(IReadOnlyList<ClientLogEntry> entries, int omitted)
    {
        var text = new StringBuilder($"Client logged {entries.Count + omitted} error(s) during this call");
        if (omitted > 0)
        {
            text.Append($" ({omitted} not shown)");
        }

        text.Append(':');
        foreach (ClientLogEntry entry in entries)
        {
            text.Append($"\n- [{entry.Level}] {entry.Category}: {entry.Message}");
            if (entry.Exception is { } exception)
            {
                text.Append($" ({exception})");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The call-tool filter: begins a collector around the tool call and, when the client logged errors during it, shows their
    /// <see cref="Format"/>ted block. A result gets it as one more text block, on a success and an error result alike. A tool's
    /// <see cref="McpException"/> passes through the filters as an exception (the SDK turns it into an error result outside
    /// them), so it is rethrown with the block after a blank line at the end of its message; a protocol error passes unchanged.
    /// Any other exception of the tool's is logged and, when the collector holds errors, answered as the error result the SDK
    /// would have built plus the block — a call that failed after reaching the client is where the client's errors matter most.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            Collector collector = Begin();
            CallToolResult result;
            try
            {
                result = await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (McpException ex) when (ex is not McpProtocolException)
            {
                string? block = FormatIfAny(collector);
                if (block is null)
                {
                    throw;
                }

                throw new McpException($"{ex.Message}\n\n{block}", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not McpException)
            {
                string? block = FormatIfAny(collector);
                if (block is null)
                {
                    throw;
                }

                ILogger? logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(nameof(PipeCallErrors));
                logger?.LogError(ex, "Tool {Tool} threw {ExceptionType} after its pipe calls", context.Params.Name, ex.GetType().Name);
                return new CallToolResult
                {
                    IsError = true,
                    Content =
                    [
                        new TextContentBlock { Text = $"An error occurred invoking '{context.Params.Name}'." },
                        new TextContentBlock { Text = block },
                    ],
                };
            }

            if (FormatIfAny(collector) is { } text)
            {
                result.Content.Add(new TextContentBlock { Text = text });
            }

            return result;
        };

    /// <summary>
    /// The registration every server and its tests use: the call-tool filter that appends the client's logged errors to every
    /// tool result.
    /// </summary>
    public static IMcpServerBuilder WithPipeCallErrors(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));

    private static string? FormatIfAny(Collector collector)
    {
        lock (collector.Entries)
        {
            return (collector.Entries.Count == 0) && (collector.Omitted == 0) ? null : Format(collector.Entries, collector.Omitted);
        }
    }
}

using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp;

/// <summary>
/// Maps short ids (<c>e1</c>, <c>e2</c>, …) to what they name — a UI Automation element or a node of a YAAT client's
/// automation pipe. Registered as a singleton because MCP tool classes are constructed per invocation: the ids an agent
/// reads from one tool must resolve in the next one. Both backends draw from one counter, so an id names exactly one of
/// them and a tool that drives the other can refuse it.
/// </summary>
/// <param name="logger">Server logger; every diagnostic goes to stderr because stdout carries the protocol.</param>
public sealed class ElementRegistry(ILogger<ElementRegistry> logger)
{
    /// <summary>
    /// The message a caller gets when an id's element or pipe node is gone; <c>{0}</c> is the id. One wording for both
    /// backends, so an agent cannot tell — and need not — which one an id belonged to.
    /// </summary>
    internal const string GoneMessageFormat =
        "Element '{0}' is gone — its window or process has exited; call list_windows or find_elements again for a fresh id";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ElementRef> _refsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idsByRuntimeId = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Pid, int NodeId), string> _idsByPipeNode = [];
    private int _lastId;

    /// <summary>Returns the id for an element, reusing the id an equal runtime id was registered under.</summary>
    public string Register(AutomationElement element)
    {
        string? runtimeKey = TryRuntimeKey(element);
        lock (_gate)
        {
            if ((runtimeKey is not null) && _idsByRuntimeId.TryGetValue(runtimeKey, out string? knownId))
            {
                _refsById[knownId] = new UiaElementRef(element);
                return knownId;
            }

            string id = NextId();
            _refsById[id] = new UiaElementRef(element);
            if (runtimeKey is not null)
            {
                _idsByRuntimeId[runtimeKey] = id;
            }

            return id;
        }
    }

    /// <summary>Returns the id for one node of a client's automation pipe, reusing the id an equal (pid, node id) was registered under.</summary>
    public string Register(int pid, int nodeId)
    {
        (int Pid, int NodeId) key = (pid, nodeId);
        lock (_gate)
        {
            if (_idsByPipeNode.TryGetValue(key, out string? knownId))
            {
                return knownId;
            }

            string id = NextId();
            _refsById[id] = new PipeNodeRef(pid, nodeId);
            _idsByPipeNode[key] = id;
            return id;
        }
    }

    /// <summary>The element or pipe node an id was registered for, or throws a message the agent can act on.</summary>
    public ElementRef Resolve(string id)
    {
        lock (_gate)
        {
            if (!_refsById.TryGetValue(id, out ElementRef? reference))
            {
                throw new McpException($"Unknown element id '{id}' — ids come from find_elements/dump_tree and die with the server process");
            }

            return reference;
        }
    }

    /// <summary>
    /// The UI Automation element an id was registered for, or throws a message the agent can act on. An id registered for
    /// a client's automation pipe is refused: this tool drives UI Automation only.
    /// </summary>
    public AutomationElement ResolveUia(string id)
    {
        if (Resolve(id) is not UiaElementRef reference)
        {
            throw new McpException($"Element '{id}' belongs to a YAAT client driven over its automation pipe; this tool cannot use it yet.");
        }

        AutomationElement element = reference.Element;
        try
        {
            _ = element.Current.ControlType;
        }
        catch (ElementNotAvailableException ex)
        {
            logger.LogDebug(ex, "Element {ElementId} is no longer available", id);
            throw new McpException(GoneMessage(id));
        }
        catch (ElementNotEnabledException ex)
        {
            logger.LogDebug(ex, "Element {ElementId} is disabled", id);
            throw new McpException($"Element '{id}' is disabled — it accepts no input until the application enables it");
        }
        catch (COMException ex)
        {
            logger.LogDebug(ex, "The provider behind element {ElementId} did not respond", id);
            throw new McpException(
                $"Element '{id}': the UI Automation provider did not respond: 0x{ex.HResult:X8}. The target is busy, shutting down, or running at a higher integrity level"
            );
        }

        return element;
    }

    /// <summary>The gone message for <paramref name="id"/>, shared by the UI Automation and the pipe path.</summary>
    internal static string GoneMessage(string id) => string.Format(CultureInfo.InvariantCulture, GoneMessageFormat, id);

    private string NextId()
    {
        _lastId++;
        return $"e{_lastId}";
    }

    private string? TryRuntimeKey(AutomationElement element)
    {
        try
        {
            int[]? runtimeId = element.GetRuntimeId();
            return runtimeId is null ? null : string.Join('.', runtimeId);
        }
        catch (Exception ex) when ((ex is ElementNotAvailableException) || (ex is COMException))
        {
            logger.LogDebug(ex, "Element vanished before its runtime id could be read; registering it without de-duplication");
            return null;
        }
    }
}

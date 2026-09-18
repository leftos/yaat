using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp;

/// <summary>
/// Maps short ids (<c>e1</c>, <c>e2</c>, …) to live UI Automation elements. Registered as a singleton because MCP tool
/// classes are constructed per invocation: the ids an agent reads from one tool must resolve in the next one.
/// </summary>
/// <param name="logger">Server logger; every diagnostic goes to stderr because stdout carries the protocol.</param>
public sealed class ElementRegistry(ILogger<ElementRegistry> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AutomationElement> _elementsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idsByRuntimeId = new(StringComparer.Ordinal);
    private int _lastId;

    /// <summary>Returns the id for an element, reusing the id an equal runtime id was registered under.</summary>
    public string Register(AutomationElement element)
    {
        string? runtimeKey = TryRuntimeKey(element);
        lock (_gate)
        {
            if ((runtimeKey is not null) && _idsByRuntimeId.TryGetValue(runtimeKey, out string? knownId))
            {
                _elementsById[knownId] = element;
                return knownId;
            }

            _lastId++;
            string id = $"e{_lastId}";
            _elementsById[id] = element;
            if (runtimeKey is not null)
            {
                _idsByRuntimeId[runtimeKey] = id;
            }

            return id;
        }
    }

    /// <summary>Returns the element an id was registered for, or throws a message the agent can act on.</summary>
    public AutomationElement Resolve(string id)
    {
        AutomationElement? element;
        lock (_gate)
        {
            if (!_elementsById.TryGetValue(id, out element))
            {
                throw new McpException($"Unknown element id '{id}' — ids come from find_elements/dump_tree and die with the server process");
            }
        }

        try
        {
            _ = element.Current.ControlType;
        }
        catch (ElementNotAvailableException ex)
        {
            logger.LogDebug(ex, "Element {ElementId} is no longer available", id);
            throw new McpException(
                $"Element '{id}' is gone — its window or process has exited; call list_windows or find_elements again for a fresh id"
            );
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

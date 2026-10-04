using System.Windows.Automation;

namespace Yaat.ClientDriver.Mcp;

/// <summary>
/// What a short element id points at: a UI Automation element of any process, or one node of a YAAT client's automation
/// tree reached over that client's pipe. An id belongs to exactly one of the two for its whole life, so a tool can tell
/// which backend an id wants before it acts on it.
/// </summary>
public abstract record ElementRef;

/// <summary>A UI Automation element, for the tools that drive UI Automation.</summary>
/// <param name="Element">The live UI Automation element the id was registered for.</param>
public sealed record UiaElementRef(AutomationElement Element) : ElementRef;

/// <summary>A node of a YAAT client's automation tree, for the tools that drive that client over its pipe.</summary>
/// <param name="Pid">The client process whose pipe owns the node.</param>
/// <param name="NodeId">The node id the client's own tree handed out.</param>
public sealed record PipeNodeRef(int Pid, int NodeId) : ElementRef;

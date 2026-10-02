using ModelContextProtocol;
using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>
/// The one place a tool's pipe work for an element id happens: it turns a registered <see cref="PipeNodeRef"/> into a call
/// on the client's automation pipe and maps every way that call can fail onto wording an agent can act on. The pipe's own
/// gone message is the UI Automation path's, so an agent reads one story whichever backend an id belongs to.
/// </summary>
public static class PipeCalls
{
    /// <summary>
    /// Sends <paramref name="method"/> to the client that owns <paramref name="node"/> and deserializes the answer as
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <param name="directory">The directory that finds and caches the client pipes.</param>
    /// <param name="element">The registered id and the client pipe node it names.</param>
    /// <param name="method">The host method, e.g. <c>get_tree</c> (see <see cref="ProtocolMethods"/>).</param>
    /// <param name="parameters">The request's params object, or null for a method that takes none.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <exception cref="McpException">
    /// The pid has no usable pipe, the pipe closed, or the host answered with a coded error; a stale-node error is
    /// reported in the UI Automation path's own gone wording. Any other failure propagates unchanged.
    /// </exception>
    public static Task<T> SendForElementAsync<T>(
        PipeDirectory directory,
        PipeElement element,
        string method,
        object? parameters,
        CancellationToken ct
    ) => SendAsync<T>(directory, element.Node.Pid, element.Id, method, parameters, ct);

    /// <summary>
    /// Sends <paramref name="method"/> to the client of <paramref name="pid"/>, for a call that names no element (keys typed
    /// into whatever has the focus), and deserializes the answer as <typeparamref name="T"/>.
    /// </summary>
    /// <param name="directory">The directory that finds and caches the client pipes.</param>
    /// <param name="pid">The client's process id.</param>
    /// <param name="method">The host method, e.g. <c>send_keys</c> (see <see cref="ProtocolMethods"/>).</param>
    /// <param name="parameters">The request's params object, or null for a method that takes none.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <exception cref="McpException">The pid has no usable pipe, the pipe closed, or the host answered with a coded error.</exception>
    public static Task<T> SendForPidAsync<T>(PipeDirectory directory, int pid, string method, object? parameters, CancellationToken ct) =>
        SendAsync<T>(directory, pid, null, method, parameters, ct);

    /// <summary>
    /// The send both entry points share; <paramref name="elementId"/> is null for a pid-level call, whose messages name the
    /// pid instead. A successful call records the pid as the directory's last target.
    /// </summary>
    private static async Task<T> SendAsync<T>(
        PipeDirectory directory,
        int pid,
        string? elementId,
        string method,
        object? parameters,
        CancellationToken ct
    )
    {
        PipeClient client = await directory.TryGetAsync(pid, ct).ConfigureAwait(false) ?? throw PipeClosed(directory, elementId, pid);

        T result;
        try
        {
            result = await client.SendAsync<T>(method, parameters, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Another caller or another thread forgot the client between this lookup and the send, so the pipe is closed.
            throw PipeClosed(directory, elementId, pid);
        }
        catch (PipeRemoteException ex)
            when ((elementId is not null) && string.Equals(ex.Code, AutomationErrorCodes.StaleNode, StringComparison.Ordinal))
        {
            throw new McpException(ElementRegistry.GoneMessage(elementId));
        }
        catch (PipeRemoteException ex)
        {
            throw new McpException(ex.Message);
        }

        directory.RememberTarget(pid);
        return result;
    }

    /// <summary>
    /// The routing decision for a tool that takes a pid: the client that pid's automation pipe belongs to, or null when
    /// it has none and the tool falls back to UI Automation. Does not send anything.
    /// </summary>
    public static async Task<PipeClient?> TryRouteAsync(PipeDirectory directory, int pid, CancellationToken ct) =>
        await directory.TryGetAsync(pid, ct).ConfigureAwait(false);

    /// <summary>
    /// The one <c>get_tree</c> node rooted at <paramref name="element"/>, with the children <paramref name="parameters"/>'
    /// depth asks for.
    /// </summary>
    /// <param name="directory">The directory that finds and caches the client pipes.</param>
    /// <param name="element">The registered id and the client pipe node to root the tree at.</param>
    /// <param name="parameters">The <c>get_tree</c> params: <c>nodeId</c>, and optionally <c>depth</c> and <c>treeKind</c>.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <exception cref="McpException">As <see cref="SendForElementAsync{T}"/>.</exception>
    public static async Task<NodeInfo> GetNodeAsync(PipeDirectory directory, PipeElement element, object parameters, CancellationToken ct)
    {
        List<NodeInfo> roots = await SendForElementAsync<List<NodeInfo>>(directory, element, ProtocolMethods.GetTree, parameters, ct)
            .ConfigureAwait(false);
        return roots.Single();
    }

    /// <summary>
    /// The pipe-closed error. A pid-level call reaches for the remembered pid, so a closed pipe there also stops the pid being
    /// remembered: an eviction that landed between a send and its <see cref="PipeDirectory.RememberTarget"/> costs one failed
    /// call, not every later one.
    /// </summary>
    private static McpException PipeClosed(PipeDirectory directory, string? elementId, int pid)
    {
        if (elementId is null)
        {
            directory.ForgetTarget(pid);
        }

        return new McpException(PipeClosedMessage(elementId, pid));
    }

    private static string PipeClosedMessage(string? elementId, int pid) =>
        (elementId is null)
            ? $"The automation pipe for pid {pid} closed; call list_windows again, or pass an element id"
            : $"Element '{elementId}' is gone — the automation pipe for pid {pid} closed; call list_windows again for a fresh id";
}

/// <summary>A registered element id and the client pipe node it names: the two a pipe call needs of an element.</summary>
/// <param name="Id">The short id the registry handed out, named in the messages a caller acts on.</param>
/// <param name="Node">The client pipe node that id resolves to.</param>
public sealed record PipeElement(string Id, PipeNodeRef Node);

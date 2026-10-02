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
    public static async Task<T> SendForElementAsync<T>(
        PipeDirectory directory,
        PipeElement element,
        string method,
        object? parameters,
        CancellationToken ct
    )
    {
        PipeClient client =
            await directory.TryGetAsync(element.Node.Pid, ct).ConfigureAwait(false)
            ?? throw new McpException(PipeClosedMessage(element.Id, element.Node.Pid));

        try
        {
            return await client.SendAsync<T>(method, parameters, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Another caller or another thread forgot the client between this lookup and the send, so the pipe is closed.
            throw new McpException(PipeClosedMessage(element.Id, element.Node.Pid));
        }
        catch (PipeRemoteException ex) when (string.Equals(ex.Code, AutomationErrorCodes.StaleNode, StringComparison.Ordinal))
        {
            throw new McpException(ElementRegistry.GoneMessage(element.Id));
        }
        catch (PipeRemoteException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// The routing decision for a tool that takes a pid: the client that pid's automation pipe belongs to, or null when
    /// it has none and the tool falls back to UI Automation. Does not send anything.
    /// </summary>
    public static async Task<PipeClient?> TryRouteAsync(PipeDirectory directory, int pid, CancellationToken ct) =>
        await directory.TryGetAsync(pid, ct).ConfigureAwait(false);

    private static string PipeClosedMessage(string elementId, int pid) =>
        $"Element '{elementId}' is gone — the automation pipe for pid {pid} closed; call list_windows again for a fresh id";
}

/// <summary>A registered element id and the client pipe node it names: the two a pipe call needs of an element.</summary>
/// <param name="Id">The short id the registry handed out, named in the messages a caller acts on.</param>
/// <param name="Node">The client pipe node that id resolves to.</param>
public sealed record PipeElement(string Id, PipeNodeRef Node);

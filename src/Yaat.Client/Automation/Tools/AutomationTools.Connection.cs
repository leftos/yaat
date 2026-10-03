using System.ComponentModel;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    /// <summary>How long <c>connect</c> and <c>create_room</c> wait for the client to get there.</summary>
    private const int SessionTimeoutSeconds = 30;

    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(SessionTimeoutSeconds);

    /// <summary>Why <c>connect</c> cannot run now, or null: the client holds one server connection at a time.</summary>
    public string? AlreadyConnected()
    {
        if (_viewModel.IsConnecting)
        {
            return "A connection attempt is already in progress.";
        }

        if (!_viewModel.IsConnected)
        {
            return null;
        }

        string url = string.IsNullOrEmpty(_viewModel.ConnectedServerUrl) ? "a server" : _viewModel.ConnectedServerUrl;
        return $"Already connected to {url}; disconnect first.";
    }

    /// <summary>Why <c>create_room</c> cannot run now, or null.</summary>
    public string? CannotCreateRoom()
    {
        if (NotConnected() is { } notConnected)
        {
            return notConnected;
        }

        if (_viewModel.IsInRoom)
        {
            return "Already in a room; leave it first.";
        }

        if (_viewModel.CreateRoomCommand.IsRunning)
        {
            return "A room creation is still in progress.";
        }

        return _viewModel.IsNonMentor ? "Signed in without mentor or instructor rights: only a mentor or instructor can create a room." : null;
    }

    /// <summary>
    /// Connects through the same method as <c>--autoconnect</c> (version check, VATSIM sign-in, hub connection), once, and answers
    /// when the client is connected or the attempt has failed. A failed attempt is a <see cref="AppToolOutcome.Done"/> naming the
    /// failure, as <c>load_recording</c> reports a failed load: the tool ran.
    /// </summary>
    [AutomationTool(
        "connect",
        "Connects the client to a YAAT server, as --autoconnect does, and waits until it is connected. The 30 s limit "
            + "includes any VATSIM sign-in in the browser.",
        nameof(AlreadyConnected)
    )]
    public async Task<AppToolOutcome> Connect([Description("The server's URL, e.g. http://localhost:5130.")] string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || ((uri.Scheme != Uri.UriSchemeHttp) && (uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new AppToolArgumentException("url", $"'url' must be an absolute http or https URL, such as http://localhost:5130, not '{url}'.");
        }

        using var timeout = new CancellationTokenSource(SessionTimeout);
        string? error = await _viewModel.AttemptConnectAsync(url, timeout.Token);
        if (error is not null)
        {
            // As --autoconnect does: the attempt's own answer becomes the status line the user sees.
            _viewModel.StatusText = error;
        }

        return _viewModel.IsConnected
            ? AppToolOutcome.Done($"Connected to {url}.")
            : AppToolOutcome.Done($"Could not connect to {url}: {error ?? _viewModel.StatusText}");
    }

    /// <summary>
    /// Creates a room for <paramref name="artccId"/> through the same command as the room list's Create button, and answers when
    /// the client has joined it, or with a <see cref="AppToolOutcome.Done"/> naming the failure when the server refused or did not
    /// answer in time.
    /// </summary>
    [AutomationTool(
        "create_room",
        "Creates a training room for an ARTCC and joins it, as the room list's Create button does.",
        nameof(CannotCreateRoom)
    )]
    public async Task<AppToolOutcome> CreateRoom([Description("The room's ARTCC: three letters, e.g. ZOA.")] string artccId)
    {
        string artcc = artccId.Trim().ToUpperInvariant();
        if ((artcc.Length != 3) || !artcc.All(char.IsAsciiLetterUpper))
        {
            throw new AppToolArgumentException("artccId", $"'artccId' must be a three-letter ARTCC id, such as ZOA, not '{artccId}'.");
        }

        if (!_viewModel.PermittedArtccs.Contains(artcc, StringComparer.OrdinalIgnoreCase))
        {
            string permitted = _viewModel.PermittedArtccs.Count == 0 ? "none" : string.Join(", ", _viewModel.PermittedArtccs);
            throw new AppToolArgumentException("artccId", $"'artccId' {artcc} is not an ARTCC you may create a room for; permitted: {permitted}.");
        }

        _viewModel.SelectedCreateArtccId = artcc;
        try
        {
            await _viewModel.CreateRoomCommand.ExecuteAsync(null).WaitAsync(SessionTimeout);
        }
        catch (TimeoutException)
        {
            return AppToolOutcome.Done(
                $"Could not create a {artcc} room: no answer from the server after {SessionTimeoutSeconds} s; "
                    + "the room may still be created — check list_app_tools."
            );
        }

        return _viewModel.IsInRoom
            ? AppToolOutcome.Done($"Created a {artcc} room.")
            : AppToolOutcome.Done($"Could not create a {artcc} room: {_viewModel.StatusText}");
    }
}

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.Services.Speech;

/// <summary>An installer's observable values at one moment.</summary>
/// <param name="IsInstalled">Whether the installed files are on disk.</param>
/// <param name="IsBusy">Whether an install or removal runs.</param>
/// <param name="Progress">0.0 to 1.0 through the running install.</param>
/// <param name="StatusMessage">The installer's status text.</param>
public sealed record InstallerState(bool IsInstalled, bool IsBusy, double Progress, string StatusMessage);

/// <summary>
/// A UI-thread copy of an installer's state. The installers (<see cref="SttModelInstaller"/>, <see cref="SherpaCudaInstaller"/>)
/// raise <see cref="INotifyPropertyChanged.PropertyChanged"/> on their worker thread; each raise here only posts a callback,
/// and the callback reads the installer and writes the copy, so the view reads values that change only on the thread
/// <c>post</c> runs on. Callbacks run in post order; after <see cref="Dispose"/> none writes. It subscribes before it reads
/// its starting values, so a change raised during construction still posts a callback. Construct and dispose it on the
/// thread <c>post</c> runs callbacks on.
/// </summary>
public sealed partial class UiThreadMirror : ObservableObject, IDisposable
{
    private readonly INotifyPropertyChanged _source;
    private readonly Func<InstallerState> _read;
    private readonly Action<Action> _post;
    private bool _disposed;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusMessage;

    /// <summary>
    /// Subscribes to <paramref name="source"/> and takes its current values. Construct on the thread <paramref name="post"/>
    /// runs callbacks on.
    /// </summary>
    /// <param name="source">The installer whose changes are mirrored.</param>
    /// <param name="read">Reads the installer's current values.</param>
    /// <param name="post">Queues a callback on the UI thread; production passes <c>a =&gt; Dispatcher.UIThread.Post(a)</c>.</param>
    public UiThreadMirror(INotifyPropertyChanged source, Func<InstallerState> read, Action<Action> post)
    {
        _source = source;
        _read = read;
        _post = post;
        source.PropertyChanged += OnSourceChanged;
        InstallerState state = read();
        _isInstalled = state.IsInstalled;
        _isBusy = state.IsBusy;
        _progress = state.Progress;
        _statusMessage = state.StatusMessage;
    }

    /// <summary>A mirror of <paramref name="installer"/>.</summary>
    /// <param name="installer">The model installer.</param>
    /// <param name="post">Queues a callback on the UI thread.</param>
    public static UiThreadMirror Of(SttModelInstaller installer, Action<Action> post) =>
        new(installer, () => new InstallerState(installer.IsInstalled, installer.IsBusy, installer.Progress, installer.StatusMessage), post);

    /// <summary>A mirror of <paramref name="installer"/>.</summary>
    /// <param name="installer">The GPU runtime installer.</param>
    /// <param name="post">Queues a callback on the UI thread.</param>
    public static UiThreadMirror Of(SherpaCudaInstaller installer, Action<Action> post) =>
        new(installer, () => new InstallerState(installer.IsInstalled, installer.IsBusy, installer.Progress, installer.StatusMessage), post);

    /// <summary>Unsubscribes; a callback already posted writes nothing when it runs.</summary>
    public void Dispose()
    {
        _disposed = true;
        _source.PropertyChanged -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => _post(Refresh);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        InstallerState state = _read();
        IsInstalled = state.IsInstalled;
        IsBusy = state.IsBusy;
        Progress = state.Progress;
        StatusMessage = state.StatusMessage;
    }
}

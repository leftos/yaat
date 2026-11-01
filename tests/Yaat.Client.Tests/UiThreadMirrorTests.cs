using System.ComponentModel;
using System.Runtime.CompilerServices;
using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The installer-state mirror with a queue-backed post: a worker-thread raise reaches the mirror only when the queue runs,
/// in the order it was posted, and never after the mirror is disposed.
/// </summary>
public sealed class UiThreadMirrorTests
{
    private readonly Queue<Action> _queue = new();

    [Fact]
    public void Starts_From_The_Source_Values()
    {
        var source = new FakeInstaller { IsInstalled = true, StatusMessage = "Ready" };

        using UiThreadMirror mirror = NewMirror(source);

        Assert.True(mirror.IsInstalled);
        Assert.Equal("Ready", mirror.StatusMessage);
    }

    [Fact]
    public void A_Raise_Does_Nothing_Until_The_Queue_Runs()
    {
        var source = new FakeInstaller();
        using UiThreadMirror mirror = NewMirror(source);
        List<string?> raised = [];
        mirror.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        source.IsBusy = true;
        source.Progress = 0.25;
        source.StatusMessage = "Downloading";

        Assert.False(mirror.IsBusy);
        Assert.Equal(0, mirror.Progress);
        Assert.Equal("", mirror.StatusMessage);
        Assert.Empty(raised);

        Drain();

        Assert.True(mirror.IsBusy);
        Assert.Equal(0.25, mirror.Progress);
        Assert.Equal("Downloading", mirror.StatusMessage);
        Assert.Contains(nameof(UiThreadMirror.Progress), raised);
    }

    [Fact]
    public void Two_Queued_Raises_Show_The_Latest_Value_From_The_First_Callback()
    {
        var source = new FakeInstaller();
        using UiThreadMirror mirror = NewMirror(source);
        List<string> seen = [];
        mirror.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UiThreadMirror.StatusMessage))
            {
                seen.Add(mirror.StatusMessage);
            }
        };

        source.StatusMessage = "first";
        Action first = _queue.Dequeue();
        source.StatusMessage = "second";
        Action second = _queue.Dequeue();

        first();
        Assert.Equal("second", mirror.StatusMessage);
        second();

        Assert.Equal(["second"], seen);
        Assert.Equal("second", mirror.StatusMessage);
    }

    [Fact]
    public void Each_Callback_Reads_The_Source_When_It_Runs()
    {
        var source = new FakeInstaller();
        using UiThreadMirror mirror = NewMirror(source);
        List<double> seen = [];
        mirror.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UiThreadMirror.Progress))
            {
                seen.Add(mirror.Progress);
            }
        };

        source.Progress = 0.1;
        _queue.Dequeue()();
        source.Progress = 0.2;
        _queue.Dequeue()();

        Assert.Equal([0.1, 0.2], seen);
    }

    [Fact]
    public void A_Change_Raised_While_The_Constructor_Reads_Still_Reaches_The_Mirror()
    {
        var source = new FakeInstaller { StatusMessage = "old" };
        bool first = true;
        InstallerState Read()
        {
            var state = new InstallerState(source.IsInstalled, source.IsBusy, source.Progress, source.StatusMessage);
            if (first)
            {
                first = false;
                source.StatusMessage = "new";
            }
            return state;
        }

        using var mirror = new UiThreadMirror(source, Read, _queue.Enqueue);
        Assert.Equal("old", mirror.StatusMessage);

        Drain();

        Assert.Equal("new", mirror.StatusMessage);
    }

    [Fact]
    public void Dispose_Before_Draining_Drops_The_Writes()
    {
        var source = new FakeInstaller();
        UiThreadMirror mirror = NewMirror(source);
        source.StatusMessage = "late";

        mirror.Dispose();
        Drain();

        Assert.Equal("", mirror.StatusMessage);
        Assert.Equal(0, source.SubscriberCount);
    }

    private UiThreadMirror NewMirror(FakeInstaller source) =>
        new(source, () => new InstallerState(source.IsInstalled, source.IsBusy, source.Progress, source.StatusMessage), _queue.Enqueue);

    private void Drain()
    {
        while (_queue.TryDequeue(out Action? action))
        {
            action();
        }
    }

    /// <summary>An installer's four observable values, raised on the calling thread as the real installers do.</summary>
    private sealed class FakeInstaller : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _propertyChanged;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add
            {
                _propertyChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _propertyChanged -= value;
                SubscriberCount--;
            }
        }

        public int SubscriberCount { get; private set; }

        public bool IsInstalled
        {
            get;
            set => Set(out field, value);
        }

        public bool IsBusy
        {
            get;
            set => Set(out field, value);
        }

        public double Progress
        {
            get;
            set => Set(out field, value);
        }

        public string StatusMessage
        {
            get;
            set => Set(out field, value);
        } = "";

        private void Set<T>(out T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

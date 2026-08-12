using System;
using System.Threading;
using System.Threading.Tasks;
using SharpHook;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Guards the #347 freeze: MainWindow.OnClosing disposes the global key hook on the UI thread,
/// and SharpHook's Dispose is a blocking call into native libuiohook <c>hook_stop()</c> with no
/// timeout. If that native teardown wedges, Dispose must still return so the app can finish
/// shutting down instead of freezing forever.
/// </summary>
public class GlobalKeyHookServiceDisposeTests
{
    /// <summary>An IGlobalHook whose Dispose blocks forever, simulating a wedged native <c>hook_stop()</c>.</summary>
    private sealed class HangingHook : IGlobalHook
    {
        private readonly ManualResetEventSlim _never = new(false);

        public bool IsRunning => true;
        public bool IsDisposed { get; private set; }

        public void Run() { }

        public Task RunAsync() => Task.CompletedTask;

        public void Stop() { }

        public void Dispose()
        {
            IsDisposed = true;
            _never.Wait();
        }

#pragma warning disable CS0067 // IGlobalHook requires the events; the fake never raises them.
        public event EventHandler<HookEventArgs>? HookEnabled;
        public event EventHandler<HookEventArgs>? HookDisabled;
        public event EventHandler<KeyboardHookEventArgs>? KeyTyped;
        public event EventHandler<KeyboardHookEventArgs>? KeyPressed;
        public event EventHandler<KeyboardHookEventArgs>? KeyReleased;
        public event EventHandler<MouseHookEventArgs>? MouseClicked;
        public event EventHandler<MouseHookEventArgs>? MousePressed;
        public event EventHandler<MouseHookEventArgs>? MouseReleased;
        public event EventHandler<MouseHookEventArgs>? MouseMoved;
        public event EventHandler<MouseHookEventArgs>? MouseDragged;
        public event EventHandler<MouseWheelHookEventArgs>? MouseWheel;
#pragma warning restore CS0067
    }

    [Fact]
    public async Task DisposeReturnsEvenWhenNativeTeardownHangs()
    {
        var service = new GlobalKeyHookService(new HangingHook());

        var dispose = Task.Run(service.Dispose, TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.True(
            completed == dispose,
            "GlobalKeyHookService.Dispose must return even when the native hook teardown hangs (#347 froze the UI thread here)"
        );
    }
}

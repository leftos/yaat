using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A view model a test builds runs none of the app's own startup loads. The suite builds hundreds of them, and each one's
/// real load is a request to vNAS, a download and a 5.5 MB NavData.dat parse that no test awaits or ends: a full run's host
/// held 37 of them in flight and 18 copies of NavData.dat in a 1 GB heap when it went off the CPU.
/// </summary>
public sealed class MainViewModelStartupLoadTests
{
    /// <summary>A hang guard under tools/test-all.ps1's 90 s stall window, so a hang fails here by name.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    [AvaloniaFact]
    public async Task A_Built_ViewModel_Sends_Nothing_To_Vnas()
    {
        using var requests = new HttpRequestRecorder();

        // The recorder sees a request this process sends, so an empty list below is the view model's doing: a refused
        // loopback connection still starts its request.
        using (var probe = new HttpClient())
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => probe.GetAsync("http://127.0.0.1:9/", TestContext.Current.CancellationToken));
        }

        Assert.Contains(requests.Uris, uri => uri.Host == "127.0.0.1");

        var vm = new MainViewModel(new FakeFilePickerService());
        Task first = await Task.WhenAny(vm.NavDataInitialization, requests.FirstVnasRequest)
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.True(
            first == vm.NavDataInitialization,
            $"The view model's startup sent {string.Join(", ", requests.Uris.Where(IsVnas))} before its nav-data load ended."
        );
        Assert.DoesNotContain(requests.Uris, IsVnas);
    }

    [AvaloniaFact]
    public async Task A_Built_ViewModel_Reads_The_Committed_NavData_After_A_Test_Replaced_It()
    {
        // A first view model installs the committed database, as an earlier test's would.
        var first = new MainViewModel(new FakeFilePickerService());
        await first.NavDataInitialization.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        // A test that installs a synthetic database and never restores it, as RadarViewModelFixFilterTests does.
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting());
        try
        {
            var vm = new MainViewModel(new FakeFilePickerService());
            await vm.NavDataInitialization.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

            Assert.Same(TestVnasData.NavigationDb, NavigationDatabase.Instance);
        }
        finally
        {
            TestVnasData.EnsureInitialized();
        }
    }

    private static bool IsVnas(Uri uri) => uri.Host.EndsWith("vnas.vatsim.net", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every HTTP request this process starts, read from HttpClient's own diagnostic events.</summary>
    private sealed class HttpRequestRecorder : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];
        private readonly List<Uri> _uris = [];
        private readonly TaskCompletionSource _vnas = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpRequestRecorder() => _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

        public IReadOnlyList<Uri> Uris
        {
            get
            {
                lock (_uris)
                {
                    return [.. _uris];
                }
            }
        }

        public Task FirstVnasRequest => _vnas.Task;

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "HttpHandlerDiagnosticListener")
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if ((value.Key != "System.Net.Http.HttpRequestOut.Start") && (value.Key != "System.Net.Http.Request"))
            {
                return;
            }

            if (value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) is HttpRequestMessage { RequestUri: { } uri })
            {
                lock (_uris)
                {
                    if (!_uris.Contains(uri))
                    {
                        _uris.Add(uri);
                    }
                }

                if (IsVnas(uri))
                {
                    _vnas.TrySetResult();
                }
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) => throw new InvalidOperationException("HttpHandlerDiagnosticListener reported an error", error);

        public void Dispose()
        {
            lock (_subscriptions)
            {
                foreach (IDisposable subscription in _subscriptions)
                {
                    subscription.Dispose();
                }
            }
        }
    }
}

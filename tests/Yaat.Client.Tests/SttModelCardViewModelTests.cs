using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>A model card's state line and command availability through a download, against a fake server the test holds open.</summary>
public sealed class SttModelCardViewModelTests : IDisposable
{
    private const string FileUrl = "https://r2.example/stt/test-model/tokens.txt";

    /// <summary>A backstop for a hung download; a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("a b");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-stt-card-tests", Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _release.TrySetResult();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        if (File.Exists(_root + ".lock"))
        {
            File.Delete(_root + ".lock");
        }
    }

    [Fact]
    public async Task StateText_ShowsTheSize_ThenProgress_ThenReady()
    {
        (SttModelCardViewModel card, SttModelInstaller installer, SttModelDownload model) = NewCard();
        Assert.Equal(SttSizeText.Total(model.TotalBytes), card.StateText);

        Task install = installer.InstallAsync(TestContext.Current.CancellationToken);
        await _requested.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(SttSizeText.Progress(card.Install!.Progress, model.TotalBytes), card.StateText);

        _release.SetResult();
        await install.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(SttSizeText.Ready(model.TotalBytes), card.StateText);
    }

    [Fact]
    public async Task Commands_FollowTheDownload()
    {
        (SttModelCardViewModel card, _, _) = NewCard();
        Assert.Equal((true, false, false), CanExecute(card));

        card.DownloadCommand.Execute(null);
        await _requested.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal((false, true, false), CanExecute(card));

        _release.SetResult();
        await card.DownloadCommand.ExecutionTask!.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal((false, false, true), CanExecute(card));
        Assert.True(card.ViewLicenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_Refused_Download_Says_Why_On_The_Card()
    {
        (SttModelCardViewModel card, _, SttModelDownload model) = NewCard();
        Directory.CreateDirectory(_root);
        using FileStream? heldByTheOtherDownload = SttModelInstaller.TryLockRoot(_root);
        Assert.NotNull(heldByTheOtherDownload);

        await card.DownloadCommand.ExecuteAsync(null);

        Assert.NotEqual(SttSizeText.Total(model.TotalBytes), card.StateText);
    }

    [Theory]
    [InlineData(SttEngine.RasrV2Fp32, "GPU, or a fast CPU")]
    [InlineData(SttEngine.RasrV2Int8, "CPU with little memory")]
    public void ARasrCard_SaysWhereItRunsBest(SttEngine engine, string bestOn)
    {
        (SttModelCardViewModel card, _, _) = NewCard(engine);

        Assert.Equal(bestOn, card.BestOnText);
    }

    [Fact]
    public void Int8Card_HasTheLessAccurateNote()
    {
        (SttModelCardViewModel card, _, _) = NewCard(SttEngine.RasrV2Int8);

        Assert.Equal(
            "Less accurate than fp32: it gets more words and callsigns wrong. Pick it when memory or download size matters more.",
            card.NoteText
        );
    }

    [Fact]
    public void Fp32Card_HasNoNote()
    {
        (SttModelCardViewModel card, _, _) = NewCard(SttEngine.RasrV2Fp32);

        Assert.Null(card.NoteText);
    }

    [Fact]
    public void TheWhisperCard_HasNoBestOn_AndNotesItsSingleHypothesisAndRemoval()
    {
        var card = SttModelCardViewModel.ForWhisper();

        Assert.Equal("", card.BestOnText);
        Assert.Equal("One hypothesis only, so accurate mode is unavailable. Removed in a later release.", card.NoteText);
    }

    private static (bool Download, bool Cancel, bool Delete) CanExecute(SttModelCardViewModel card) =>
        (card.DownloadCommand.CanExecute(null), card.CancelDownloadCommand.CanExecute(null), card.DeleteCommand.CanExecute(null));

    private (SttModelCardViewModel Card, SttModelInstaller Installer, SttModelDownload Model) NewCard() => NewCard(SttEngine.RasrV2Fp32);

    private (SttModelCardViewModel Card, SttModelInstaller Installer, SttModelDownload Model) NewCard(SttEngine engine)
    {
        var model = new SttModelDownload(
            "rasr-parakeet-v2-fp32",
            [new SttModelFile("tokens.txt", [FileUrl], Body.Length, Convert.ToHexStringLower(SHA256.HashData(Body)))]
        );
        var installer = new SttModelInstaller(new HttpClient(new HeldHandler(_requested, _release)), _root, model, TimeProvider.System);
        // Callbacks run at once, so the card reads the installer's state as soon as it changes.
        var card = SttModelCardViewModel.ForModel(engine, model, installer, _root, true, a => a());
        return (card, installer, model);
    }

    /// <summary>Signals the first request, then serves the body once the test releases it.</summary>
    private sealed class HeldHandler(TaskCompletionSource requested, TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) };
        }
    }
}

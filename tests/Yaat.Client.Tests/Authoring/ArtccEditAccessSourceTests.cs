using System.Net;
using System.Text;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// <see cref="ArtccEditAccessSource"/>: while connected it asks the hub through <see cref="ArtccEditAccess"/>, which caches
/// the answer with the signed-in session; while disconnected it answers from that cache, else the Settings ARTCC, else
/// none. A stub <see cref="HttpMessageHandler"/> plays the server's dev login so a session exists to cache on.
/// </summary>
public sealed class ArtccEditAccessSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connected_ReturnsTheHubList_AndADisconnectedCallReturnsItFromTheCache()
    {
        string server = NewServer();
        VatsimAuthClient auth = await SignedInAsync(server);
        bool connected = true;
        var source = new ArtccEditAccessSource(new ArtccEditAccess(auth, () => "ZLA"), () => connected, () => server, Hub("ZOA", "ZNY"));

        IReadOnlyList<string> online = await source.GetEditableArtccsAsync(Ct);
        connected = false;
        IReadOnlyList<string> offline = await source.GetEditableArtccsAsync(Ct);

        Assert.Equal(["ZOA", "ZNY"], online);
        Assert.Equal(["ZOA", "ZNY"], offline);
    }

    [Fact]
    public async Task Disconnected_NothingCached_ReturnsTheSettingsArtcc()
    {
        int fetches = 0;
        var source = new ArtccEditAccessSource(
            new ArtccEditAccess(new VatsimAuthClient(), () => "zoa"),
            () => false,
            NewServer,
            _ =>
            {
                fetches++;
                return Task.FromResult(new List<string> { "ZNY" });
            }
        );

        IReadOnlyList<string> artccs = await source.GetEditableArtccsAsync(Ct);

        Assert.Equal(["ZOA"], artccs);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task Disconnected_NoSettingsArtcc_ReturnsNone()
    {
        var source = new ArtccEditAccessSource(new ArtccEditAccess(new VatsimAuthClient(), () => ""), () => false, NewServer, Hub("ZNY"));

        Assert.Empty(await source.GetEditableArtccsAsync(Ct));
    }

    [Fact]
    public async Task Connected_FetchFails_ReturnsTheOfflineAnswer()
    {
        var source = new ArtccEditAccessSource(
            new ArtccEditAccess(new VatsimAuthClient(), () => "ZOA"),
            () => true,
            NewServer,
            _ => Task.FromException<List<string>>(new InvalidOperationException("The connection is not active"))
        );

        Assert.Equal(["ZOA"], await source.GetEditableArtccsAsync(Ct));
    }

    private static string NewServer() => $"https://edit-source-{Guid.NewGuid():N}.test";

    private static Func<CancellationToken, Task<List<string>>> Hub(params string[] artccs) => _ => Task.FromResult(artccs.ToList());

    private static async Task<VatsimAuthClient> SignedInAsync(string server)
    {
        var auth = new VatsimAuthClient(new HttpClient(new DevLoginStub()));
        Assert.NotNull(await auth.EnsureSignedInAsync(server, null, Ct));
        return auth;
    }

    // The server's dev login: no sign-in required, and /auth/dev answers a session for one CID.
    private sealed class DevLoginStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.RequestUri!.AbsolutePath switch
            {
                "/auth/required" => """{"required":false}""",
                "/auth/dev" => $$"""{"accessToken":"{{Jwt()}}","refreshToken":"refresh-1","cid":"1234567","name":"Test User"}""",
                _ => null,
            };
            return Task.FromResult(
                body is null
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            );
        }

        private static string Jwt()
        {
            string payload = $$"""{"exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""";
            return $"{Base64Url("""{"alg":"none"}""")}.{Base64Url(payload)}.sig";
        }

        private static string Base64Url(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

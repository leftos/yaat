using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.Tests;

/// <summary>
/// <see cref="ArtccEditAccess"/>: the ARTCCs the hub says the signed-in user may edit scenarios for are cached with the
/// stored session (so they survive a restart and a token refresh, and go with a sign-out or a new login), only on the session
/// of the CID that asked; without the hub the cached list stands in, else the Settings ARTCC. A stub
/// <see cref="HttpMessageHandler"/> plays the server's dev login and refresh.
/// </summary>
public sealed class ArtccEditAccessTests
{
    private sealed class AuthServerStub : HttpMessageHandler
    {
        /// <summary>The CID the next dev login answers with.</summary>
        public string Cid { get; set; } = "1234567";

        /// <summary>How long from now the access tokens the stub mints expire.</summary>
        public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromHours(1);

        /// <summary>Whether <c>/auth/refresh</c> succeeds; false answers 401, which forces a fresh login.</summary>
        public bool RefreshSucceeds { get; set; } = true;

        public int Refreshes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.RequestUri!.AbsolutePath switch
            {
                "/auth/required" => """{"required":false}""",
                "/auth/dev" => $$"""{"accessToken":"{{Jwt()}}","refreshToken":"refresh-1","cid":"{{Cid}}","name":"Test User"}""",
                "/auth/refresh" => Refresh(),
                _ => null,
            };
            return Task.FromResult(
                body is null
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            );
        }

        private string? Refresh()
        {
            Refreshes++;
            return RefreshSucceeds ? $$"""{"accessToken":"{{Jwt()}}","refreshToken":"refresh-2"}""" : null;
        }

        private string Jwt()
        {
            string payload = $$"""{"exp":{{DateTimeOffset.UtcNow.Add(AccessLifetime).ToUnixTimeSeconds()}}}""";
            return $"{Base64Url("""{"alg":"none"}""")}.{Base64Url(payload)}.sig";
        }

        private static string Base64Url(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string NewServer() => $"https://edit-{Guid.NewGuid():N}.test";

    private static Func<CancellationToken, Task<List<string>>> Hub(params string[] artccs) => _ => Task.FromResult(artccs.ToList());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(AuthServerStub Stub, VatsimAuthClient Auth)> SignedInAsync(string server)
    {
        var stub = new AuthServerStub();
        var auth = new VatsimAuthClient(new HttpClient(stub));
        Assert.NotNull(await auth.EnsureSignedInAsync(server, null, Ct));
        return (stub, auth);
    }

    [Fact]
    public async Task HubResult_IsCachedWithTheStoredSession_AndSurvivesAReload()
    {
        string server = NewServer();
        (AuthServerStub stub, VatsimAuthClient auth) = await SignedInAsync(server);
        try
        {
            IReadOnlyList<string> online = await new ArtccEditAccess(auth, () => "ZLA").RefreshAsync(server, Hub("ZMA", "ZOA"), Ct);

            var reloaded = new VatsimAuthClient(new HttpClient(stub));
            Assert.Equal(["ZMA", "ZOA"], online);
            Assert.Equal(["ZMA", "ZOA"], reloaded.GetEditableArtccs(server));
            Assert.Equal(["ZMA", "ZOA"], new ArtccEditAccess(reloaded, () => "ZLA").GetOffline(server));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    [Fact]
    public async Task Offline_SignedInWithoutACachedList_UsesTheSettingsArtcc()
    {
        string server = NewServer();
        (_, VatsimAuthClient auth) = await SignedInAsync(server);
        try
        {
            Assert.Equal(["ZLA"], new ArtccEditAccess(auth, () => " zla ").GetOffline(server));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    [Fact]
    public void Offline_NoSession_UsesTheSettingsArtcc_OrNothingWhenItIsBlank()
    {
        var auth = new VatsimAuthClient(new HttpClient(new AuthServerStub()));

        Assert.Equal(["ZOA"], new ArtccEditAccess(auth, () => "ZOA").GetOffline(NewServer()));
        Assert.Empty(new ArtccEditAccess(auth, () => "").GetOffline(NewServer()));
    }

    [Fact]
    public async Task HubCallFails_ReturnsTheCachedList_ElseTheSettingsArtcc()
    {
        string server = NewServer();
        (_, VatsimAuthClient auth) = await SignedInAsync(server);
        var access = new ArtccEditAccess(auth, () => "ZLA");
        try
        {
            static Task<List<string>> offline(CancellationToken _) => throw new InvalidOperationException("Not connected to server");

            Assert.Equal(["ZLA"], await access.RefreshAsync(server, offline, Ct));
            await access.RefreshAsync(server, Hub("ZOA"), Ct);
            Assert.Equal(["ZOA"], await access.RefreshAsync(server, offline, Ct));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    [Fact]
    public async Task ConnectionDroppedMidCall_FallsBack()
    {
        string server = NewServer();
        (_, VatsimAuthClient auth) = await SignedInAsync(server);
        var access = new ArtccEditAccess(auth, () => "ZLA");
        try
        {
            Assert.Equal(["ZLA"], await access.RefreshAsync(server, _ => throw new TaskCanceledException("Connection closed"), Ct));
            Assert.Equal(["ZLA"], await access.RefreshAsync(server, _ => throw new OperationCanceledException("Connection closed"), Ct));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var access = new ArtccEditAccess(new VatsimAuthClient(new HttpClient(new AuthServerStub())), () => "ZLA");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            access.RefreshAsync(NewServer(), token => Task.FromCanceled<List<string>>(token), cts.Token)
        );
    }

    [Fact]
    public async Task AnswerArrivingAfterAnotherCidSignedIn_IsNotCachedOnTheirSession()
    {
        string server = NewServer();
        (AuthServerStub stub, VatsimAuthClient auth) = await SignedInAsync(server);
        try
        {
            async Task<List<string>> SwitchUserThenAnswer(CancellationToken ct)
            {
                auth.SignOut(server);
                stub.Cid = "7654321";
                Assert.NotNull(await auth.EnsureSignedInAsync(server, null, ct));
                return ["ZMA"];
            }

            await new ArtccEditAccess(auth, () => "ZLA").RefreshAsync(server, SwitchUserThenAnswer, Ct);

            Assert.Equal("7654321", auth.GetIdentity(server)?.Cid);
            Assert.Null(auth.GetEditableArtccs(server));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    [Fact]
    public async Task SignOut_DropsTheCachedList()
    {
        string server = NewServer();
        (_, VatsimAuthClient auth) = await SignedInAsync(server);
        await new ArtccEditAccess(auth, () => "ZLA").RefreshAsync(server, Hub("ZOA"), Ct);

        auth.SignOut(server);

        Assert.Null(auth.GetEditableArtccs(server));
    }

    [Fact]
    public async Task TokenRefresh_KeepsTheCachedList()
    {
        string server = NewServer();
        var stub = new AuthServerStub { AccessLifetime = TimeSpan.FromMinutes(-5) };
        var auth = new VatsimAuthClient(new HttpClient(stub));
        try
        {
            Assert.NotNull(await auth.EnsureSignedInAsync(server, null, Ct));
            await new ArtccEditAccess(auth, () => "ZLA").RefreshAsync(server, Hub("ZOA"), Ct);

            Assert.NotNull(await auth.GetValidAccessTokenAsync(server));

            Assert.Equal(1, stub.Refreshes);
            Assert.Equal(["ZOA"], auth.GetEditableArtccs(server));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    [Fact]
    public async Task NewLogin_DropsTheCachedList()
    {
        string server = NewServer();
        (AuthServerStub stub, VatsimAuthClient auth) = await SignedInAsync(server);
        try
        {
            await new ArtccEditAccess(auth, () => "ZLA").RefreshAsync(server, Hub("ZOA"), Ct);
            stub.RefreshSucceeds = false;

            Assert.NotNull(await auth.EnsureSignedInAsync(server, null, Ct));

            Assert.Equal(1, stub.Refreshes);
            Assert.Null(auth.GetEditableArtccs(server));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    [Fact]
    public async Task SessionFileWithoutTheList_StillLoads()
    {
        string server = NewServer();
        (AuthServerStub stub, VatsimAuthClient auth) = await SignedInAsync(server);
        try
        {
            string path = YaatPaths.Combine("auth-sessions.json");
            JsonObject sessions = JsonNode.Parse(Unprotect(File.ReadAllBytes(path)))!.AsObject();
            Assert.True(sessions[server]!.AsObject().Remove("EditableArtccs"));
            File.WriteAllBytes(path, Protect(sessions.ToJsonString()));

            var reloaded = new VatsimAuthClient(new HttpClient(stub));

            Assert.Equal("1234567", reloaded.GetIdentity(server)?.Cid);
            Assert.Null(reloaded.GetEditableArtccs(server));
        }
        finally
        {
            auth.SignOut(server);
        }
    }

    // The session file's at-rest encoding, as VatsimAuthClient writes it: DPAPI on Windows, plain UTF-8 elsewhere.
    private static string Unprotect(byte[] stored) =>
        Encoding.UTF8.GetString(
            OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, optionalEntropy: null, DataProtectionScope.CurrentUser) : stored
        );

    private static byte[] Protect(string json)
    {
        byte[] plain = Encoding.UTF8.GetBytes(json);
        return OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser) : plain;
    }
}

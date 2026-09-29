using System.Net;
using System.Text;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="VatsimAuthClient"/> token refresh. The server rotates the refresh token on every
/// refresh and revokes the one just used, so two callers refreshing the same session at once must share a
/// single <c>/auth/refresh</c> call: a second call would present a revoked token. A stub
/// <see cref="HttpMessageHandler"/> plays the server's dev-login and refresh endpoints.
/// </summary>
public sealed class VatsimAuthClientTests
{
    private sealed class AuthServerStub(string expiredJwt, string freshJwt) : HttpMessageHandler
    {
        private int _refreshCount;

        /// <summary>Completed by the test once every concurrent caller has started, so the refreshes overlap.</summary>
        public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/auth/required":
                    return Json("""{"required":false}""");
                case "/auth/dev":
                    return Json($$"""{"accessToken":"{{expiredJwt}}","refreshToken":"refresh-1","cid":"1234567","name":"Test User"}""");
                case "/auth/refresh":
                    Interlocked.Increment(ref _refreshCount);
                    await ReleaseRefresh.Task.WaitAsync(cancellationToken);
                    return Json($$"""{"accessToken":"{{freshJwt}}","refreshToken":"refresh-2"}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string Jwt(DateTimeOffset expires) =>
        $"{Base64Url("""{"alg":"none"}""")}.{Base64Url($$"""{"exp":{{expires.ToUnixTimeSeconds()}}}""")}.sig";

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task Concurrent_Token_Requests_On_An_Expired_Session_Share_One_Refresh()
    {
        string server = $"https://auth-{Guid.NewGuid():N}.test";
        string freshJwt = Jwt(DateTimeOffset.UtcNow.AddHours(1));
        var stub = new AuthServerStub(Jwt(DateTimeOffset.UtcNow.AddMinutes(-5)), freshJwt);
        var client = new VatsimAuthClient(new HttpClient(stub));
        try
        {
            Assert.NotNull(await client.EnsureSignedInAsync(server, null, TestContext.Current.CancellationToken));

            Task<string?> first = client.GetValidAccessTokenAsync(server);
            Task<string?> second = client.GetValidAccessTokenAsync(server);
            stub.ReleaseRefresh.SetResult();
            string?[] tokens = await Task.WhenAll(first, second);

            Assert.Equal(1, stub.RefreshCount);
            Assert.All(tokens, token => Assert.Equal(freshJwt, token));
        }
        finally
        {
            client.SignOut(server);
        }
    }
}

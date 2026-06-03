using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using XIVRaidPlannerPlugin.Api;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class RaidPlannerClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public StubHandler(HttpStatusCode code, string body = "{}") { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ApiError.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ApiError.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, ApiError.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, ApiError.Server)]
    public async Task MapsStatusToApiError(HttpStatusCode code, ApiError expected)
    {
        var result = await RaidPlannerClient.SendForTest(new StubHandler(code), "/api/auth/me");
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
    }
}

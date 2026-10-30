using System.Net;
using System.Text;
using ChessSlackBetter.Bot.Slack;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChessSlackBetter.Bot.Tests;

public sealed class EndpointTests : IClassFixture<EndpointTests.Factory>
{
    private const string Secret = "test-signing-secret";
    private readonly Factory _factory;

    public EndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Answers_url_verification_when_signed()
    {
        var response = await PostSigned("/slack/events", """{"type":"url_verification","challenge":"abc123"}""", "application/json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("abc123", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rejects_unsigned_requests()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/slack/events", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Serves_board_images()
    {
        using var client = _factory.CreateClient();
        var fen = Uri.EscapeDataString(Chess.Position.StartFen);
        var response = await client.GetAsync($"/board.png?fen={fen}&last=e2e4&pov=b&theme=blue&pieces=fantasy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        var bad = await client.GetAsync("/board.png?fen=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private async Task<HttpResponseMessage> PostSigned(string path, string body, string contentType)
    {
        using var client = _factory.CreateClient();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        request.Headers.Add("X-Slack-Request-Timestamp", ts);
        request.Headers.Add("X-Slack-Signature", SlackSignatureVerifier.Sign(Secret, ts, body));
        return await client.SendAsync(request);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"chessbot-endpoint-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Slack:SigningSecret", Secret);
            builder.UseSetting("Slack:BotToken", "xoxb-test");
            builder.UseSetting("ChessBot:DatabasePath", _db);
        }
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Slack;

public sealed record SlackMessage(
    string Channel,
    string Text,
    JsonArray? Blocks = null,
    string? ThreadTs = null,
    bool Broadcast = false);

public interface ISlackApi
{
    /// <summary>Posts a message and returns its ts.</summary>
    Task<string> PostMessageAsync(SlackMessage message, CancellationToken ct = default);

    Task UpdateMessageAsync(string channel, string ts, string text, JsonArray? blocks, CancellationToken ct = default);

    /// <summary>
    /// Answers one user (errors, help, "not your turn"…). Posted publicly in the thread, tagging them, so everyone
    /// in the game sees what the bot said.
    /// </summary>
    Task PostReplyAsync(string channel, string user, string? threadTs, string text, CancellationToken ct = default);

    Task<string> GetBotUserIdAsync(CancellationToken ct = default);

    Task<string?> GetPermalinkAsync(string channel, string ts, CancellationToken ct = default);

    /// <summary>Best-effort display name; falls back to the user id.</summary>
    Task<string> GetUserNameAsync(string userId, CancellationToken ct = default);
}

public sealed class SlackApiException(string method, string error) : Exception($"Slack API {method} failed: {error}")
{
    public string Error { get; } = error;
}

public sealed class SlackApiClient(HttpClient http, IOptions<SlackOptions> options, ILogger<SlackApiClient> logger) : ISlackApi
{
    private readonly ConcurrentDictionary<string, string> _userNames = new();
    private string? _botUserId;

    public async Task<string> PostMessageAsync(SlackMessage message, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["channel"] = message.Channel,
            ["text"] = message.Text,
            ["unfurl_links"] = false,
            ["unfurl_media"] = false,
        };
        if (message.Blocks is not null) body["blocks"] = message.Blocks.DeepClone();
        if (message.ThreadTs is not null) body["thread_ts"] = message.ThreadTs;
        if (message.Broadcast) body["reply_broadcast"] = true;

        var response = await CallAsync("chat.postMessage", body, ct);
        return response["ts"]!.GetValue<string>();
    }

    public Task UpdateMessageAsync(string channel, string ts, string text, JsonArray? blocks, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["channel"] = channel,
            ["ts"] = ts,
            ["text"] = text,
            ["blocks"] = blocks?.DeepClone() ?? new JsonArray(),
        };
        return CallAsync("chat.update", body, ct);
    }

    public Task PostReplyAsync(string channel, string user, string? threadTs, string text, CancellationToken ct = default) =>
        PostMessageAsync(new SlackMessage(channel, $"<@{user}> {text}", ThreadTs: threadTs), ct);

    public async Task<string> GetBotUserIdAsync(CancellationToken ct = default)
    {
        if (_botUserId is null)
        {
            var response = await CallAsync("auth.test", new JsonObject(), ct);
            _botUserId = response["user_id"]!.GetValue<string>();
        }

        return _botUserId;
    }

    public async Task<string?> GetPermalinkAsync(string channel, string ts, CancellationToken ct = default)
    {
        try
        {
            var response = await GetAsync("chat.getPermalink", $"channel={Uri.EscapeDataString(channel)}&message_ts={Uri.EscapeDataString(ts)}", ct);
            return response["permalink"]?.GetValue<string>();
        }
        catch (SlackApiException ex)
        {
            logger.LogWarning(ex, "Could not get permalink for {Channel} {Ts}", channel, ts);
            return null;
        }
    }

    public async Task<string> GetUserNameAsync(string userId, CancellationToken ct = default)
    {
        if (_userNames.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        try
        {
            var response = await GetAsync("users.info", $"user={Uri.EscapeDataString(userId)}", ct);
            var user = response["user"];
            var profile = user?["profile"];
            var name = new[]
                {
                    profile?["display_name"]?.GetValue<string>(),
                    profile?["real_name"]?.GetValue<string>(),
                    user?["name"]?.GetValue<string>(),
                }
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? userId;
            _userNames[userId] = name;
            return name;
        }
        catch (SlackApiException ex)
        {
            logger.LogWarning(ex, "Could not look up user {User}", userId);
            return userId;
        }
    }

    private async Task<JsonNode> CallAsync(string method, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://slack.com/api/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.BotToken);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(method, request, ct);
    }

    private async Task<JsonNode> GetAsync(string method, string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://slack.com/api/{method}?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.BotToken);
        return await SendAsync(method, request, ct);
    }

    private async Task<JsonNode> SendAsync(string method, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        if (json["ok"]?.GetValue<bool>() != true)
        {
            throw new SlackApiException(method, json["error"]?.GetValue<string>() ?? "unknown_error");
        }

        return json;
    }
}

using System.Text.Json.Nodes;
using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Bot.Slack;
using ChessSlackBetter.Bot.Users;
using ChessSlackBetter.Chess;

namespace ChessSlackBetter.Bot.Tests;

public sealed class FakeSlackApi : ISlackApi
{
    public const string BotId = "UBOT";

    private int _nextTs = 1000;

    public List<SlackMessage> Posted { get; } = [];

    public List<(string Channel, string Ts, string Text, JsonArray? Blocks)> Updated { get; } = [];

    public List<(string Channel, string User, string? ThreadTs, string Text)> Ephemeral { get; } = [];

    public Task<string> PostMessageAsync(SlackMessage message, CancellationToken ct = default)
    {
        Posted.Add(message);
        return Task.FromResult($"{_nextTs++}.000100");
    }

    public Task UpdateMessageAsync(string channel, string ts, string text, JsonArray? blocks, CancellationToken ct = default)
    {
        Updated.Add((channel, ts, text, blocks));
        return Task.CompletedTask;
    }

    public Task PostEphemeralAsync(string channel, string user, string? threadTs, string text, CancellationToken ct = default)
    {
        Ephemeral.Add((channel, user, threadTs, text));
        return Task.CompletedTask;
    }

    public Task<string> GetBotUserIdAsync(CancellationToken ct = default) => Task.FromResult(BotId);

    public Task<string?> GetPermalinkAsync(string channel, string ts, CancellationToken ct = default) =>
        Task.FromResult<string?>($"https://example.slack.com/archives/{channel}/p{ts.Replace(".", "")}");

    public Task<string> GetUserNameAsync(string userId, CancellationToken ct = default) => Task.FromResult(userId.ToLowerInvariant());
}

public sealed class InMemoryGameRepository : IGameRepository
{
    private readonly Dictionary<(string, string), GameRecord> _games = new();

    public Task<GameRecord?> GetAsync(string channelId, string threadTs, CancellationToken ct = default) =>
        Task.FromResult(_games.GetValueOrDefault((channelId, threadTs)));

    public Task SaveAsync(GameRecord game, CancellationToken ct = default)
    {
        _games[(game.ChannelId, game.ThreadTs)] = game;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GameRecord>> GetOpenGamesAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameRecord>>(_games.Values
            .Where(g => g.IsPlayer(userId) && g.State is GameState.Challenged or GameState.Active)
            .ToList());

    public Task<IReadOnlyList<GameRecord>> GetFinishedGamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameRecord>>(_games.Values.Where(g => g.State == GameState.Finished).ToList());
}

public sealed class InMemoryUserRepository : IUserRepository
{
    public Dictionary<string, UserPreferences> Preferences { get; } = new();

    public Task<UserPreferences> GetPreferencesAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(Preferences.GetValueOrDefault(userId) ?? UserPreferences.None);

    public Task SetThemeAsync(string userId, string theme, CancellationToken ct = default)
    {
        Preferences[userId] = (Preferences.GetValueOrDefault(userId) ?? UserPreferences.None) with { Theme = theme };
        return Task.CompletedTask;
    }

    public Task SetPiecesAsync(string userId, string pieces, CancellationToken ct = default)
    {
        Preferences[userId] = (Preferences.GetValueOrDefault(userId) ?? UserPreferences.None) with { Pieces = pieces };
        return Task.CompletedTask;
    }
}

public sealed class FakeDiceJudge : IDiceJudge
{
    public DiceRuling Ruling { get; set; } = Accept(
        [new DiceEffect("transform", "e4", "", "bishop", "")],
        [new DiceEffect("remove", "e4", "", "", "")]);

    public List<string> Requests { get; } = [];

    public static DiceRuling Accept(DiceEffect[] success, DiceEffect[] failure, double successBonus = 0, double failureBonus = 0) =>
        new(true, "ok", success, failure, "Your e4 pawn becomes a bishop", "Your e4 pawn is removed", successBonus, failureBonus, "Simple material trade.");

    public Task<DiceRuling> JudgeAsync(Position position, PieceColor requester, string request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Ruling);
    }
}

public sealed class FakeDiceRoller : IDiceRoller
{
    public int Next { get; set; } = 20;

    public int RollD20() => Next;
}

public sealed class FakeAnalysisLinks : IAnalysisLinks
{
    public List<string> Imported { get; } = [];

    public Task<string?> ImportAsync(string pgn, CancellationToken ct = default)
    {
        Imported.Add(pgn);
        return Task.FromResult<string?>("https://lichess.org/abc123");
    }
}

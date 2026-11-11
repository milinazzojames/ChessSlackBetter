using System.Text.Json.Nodes;
using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Bot.Quotes;
using ChessSlackBetter.Bot.Slack;
using ChessSlackBetter.Bot.Users;
using ChessSlackBetter.Chess;
using ChessSlackBetter.Chess.ThreePlayer;

namespace ChessSlackBetter.Bot.Tests;

public sealed class FakeSlackApi : ISlackApi
{
    public const string BotId = "UBOT";

    private int _nextTs = 1000;

    public List<SlackMessage> Posted { get; } = [];

    public List<(string Channel, string Ts, string Text, JsonArray? Blocks)> Updated { get; } = [];

    public List<(string Channel, string User, string? ThreadTs, string Text)> Replies { get; } = [];

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

    public Task PostReplyAsync(string channel, string user, string? threadTs, string text, CancellationToken ct = default)
    {
        Replies.Add((channel, user, threadTs, text));
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

public sealed class InMemoryThreePlayerRepository : IThreePlayerRepository
{
    private readonly Dictionary<(string, string), ThreePlayerRecord> _games = new();

    public Task<ThreePlayerRecord?> GetAsync(string channelId, string threadTs, CancellationToken ct = default) =>
        Task.FromResult(_games.GetValueOrDefault((channelId, threadTs)));

    public Task SaveAsync(ThreePlayerRecord game, CancellationToken ct = default)
    {
        _games[(game.ChannelId, game.ThreadTs)] = game;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ThreePlayerRecord>> GetOpenGamesAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ThreePlayerRecord>>(_games.Values
            .Where(g => g.IsPlayer(userId) && g.State is GameState.Challenged or GameState.Active)
            .ToList());

    public Task<IReadOnlyList<ThreePlayerRecord>> GetFinishedGamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ThreePlayerRecord>>(_games.Values.Where(g => g.State == GameState.Finished).ToList());
}

public sealed class FakeDiceJudge : IDiceJudge
{
    public DiceRuling Ruling { get; set; } = Accept(
        [new DiceEffect("transform", "e4", "", "bishop", "")],
        [new DiceEffect("remove", "e4", "", "", "")]);

    public List<string> Requests { get; } = [];

    public static DiceRuling Accept(DiceEffect[] success, DiceEffect[] failure, double successBonus = 0, double failureBonus = 0) =>
        AcceptOptions([new DiceOption(success, "")], [new DiceOption(failure, "")], successBonus, failureBonus);

    public static DiceRuling AcceptOptions(DiceOption[] success, DiceOption[] failure, double successBonus = 0, double failureBonus = 0) =>
        new(true, "ok", success, failure, "Your e4 pawn becomes a bishop", "Your e4 pawn is removed", successBonus, failureBonus, "Simple material trade.");

    public Task<DiceRuling> JudgeAsync(Position position, PieceColor requester, string request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Ruling);
    }

    public Task<DiceRuling> JudgeThreePlayerAsync(TriPosition position, TriColor requester, string request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Ruling);
    }

    public Riddle NextRiddle { get; set; } = new("I speak without a mouth and hear without ears. What am I?", "echo", ["echoes"]);

    public List<string> RiddleThemes { get; } = [];

    public Task<Riddle> WriteRiddleAsync(string theme, IReadOnlyList<string> usedAnswers, CancellationToken ct = default)
    {
        RiddleThemes.Add(theme);
        return Task.FromResult(NextRiddle);
    }

    /// <summary>Custom rule calls, and the results to hand back in order (the last one repeats).</summary>
    public List<CustomRuleCall> RuleCalls { get; } = [];

    public Queue<CustomRuleResult> RuleResults { get; } = new();

    private CustomRuleResult? _lastRuleResult;

    public Task<CustomRuleResult> RunCustomRuleAsync(CustomRuleCall call, CancellationToken ct = default)
    {
        RuleCalls.Add(call);
        _lastRuleResult = RuleResults.Count > 0 ? RuleResults.Dequeue() : _lastRuleResult ?? Quiet(call.Memory);
        return Task.FromResult(_lastRuleResult);
    }

    public static CustomRuleResult Quiet(string memory) => new([], memory, "", "", "", "", false);
}

public sealed class FakeDiceRoller : IDiceRoller
{
    public int Next { get; set; } = 20;

    public int RollD20() => Next;

    public int NextPick { get; set; }

    public int Pick(int count) => Math.Min(NextPick, count - 1);
}

/// <summary>Stays quiet unless a test sets <see cref="Next"/>, so quotes never turn up in other tests.</summary>
public sealed class FakePhilosopher : IPhilosopher
{
    public Quote? Next { get; set; }

    public List<string> Situations { get; } = [];

    public Task<Quote?> MaybeQuoteAsync(string situation, CancellationToken ct = default)
    {
        Situations.Add(situation);
        return Task.FromResult(Next);
    }
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

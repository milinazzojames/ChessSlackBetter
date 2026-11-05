using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Chess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Tests;

public class Chess960GameTests
{
    private const string Channel = "C1";
    private const string Alice = "UALICE";
    private const string Bob = "UBOB";

    private readonly FakeSlackApi _slack = new();
    private readonly InMemoryGameRepository _games = new();
    private readonly FakeAnalysisLinks _analysis = new();
    private readonly ChessBotService _bot;
    private int _ts = 1;

    public Chess960GameTests()
    {
        var options = Options.Create(new ChessBotOptions { PublicBaseUrl = "https://chess.example.com" });
        _bot = new ChessBotService(_slack, _games, new InMemoryUserRepository(), new InMemoryThreePlayerRepository(), _analysis, new FakeDiceJudge(), new FakeDiceRoller(),
            options, TimeProvider.System, NullLogger<ChessBotService>.Instance);
    }

    [Fact]
    public async Task Challenge_picks_a_shuffled_start_and_shows_it()
    {
        await Mention(Alice, $"chess960 <@{Bob}> white");

        var record = (await _games.GetOpenGamesAsync(Bob)).Single();
        Assert.Equal(GameMode.Chess960, record.Mode);
        Assert.InRange(record.Chess960Number!.Value, 0, Chess960.Count - 1);
        Assert.NotEqual(Chess960.StandardNumber, record.Chess960Number);

        var card = _slack.Posted.Single();
        Assert.Contains("*Chess960*", card.Text);
        var rank = string.Join(' ', Chess960.BackRank(record.Chess960Number.Value).ToCharArray());
        Assert.Contains($"#{record.Chess960Number}", card.Blocks!.ToJsonString());
        Assert.Contains(rank, card.Blocks.ToJsonString());
    }

    [Fact]
    public async Task Games_are_played_from_their_start_and_castle()
    {
        // BBQNRKNR: after the g-knights come out, both sides can castle short.
        var number = Enumerable.Range(0, Chess960.Count).Single(n => Chess960.BackRank(n) == "BBQNRKNR");
        var thread = await StartGame(number);

        var board = _slack.Posted[^1].Blocks!.ToJsonString();
        Assert.Contains(Uri.EscapeDataString(Chess960.StartFen(number)), board);
        Assert.Contains("Chess960 #", board);

        foreach (var (player, move) in new[] { (Alice, "Nf3"), (Bob, "Nf6"), (Alice, "O-O") })
        {
            await Say(player, move, thread);
        }

        // f8g8 could be a castle or a plain king step, so the bot asks rather than guessing.
        await Say(Bob, "f8g8", thread);
        Assert.Contains("could be castling or just a king move", _slack.Ephemeral.Single().Text);
        await Say(Bob, "O-O", thread);

        var record = (await _games.GetAsync(Channel, thread))!;
        Assert.Equal(["g1f3", "g8f6", "f1h1", "f8h8"], record.Moves);
        Assert.Equal("bbqnrrk1/pppppppp/5n2/8/8/5N2/PPPPPPPP/BBQNRRK1 w - - 4 3", record.LoadGame().Current.ToFen());
    }

    [Fact]
    public async Task Finished_games_go_to_lichess_as_chess960_and_have_their_own_leaderboard()
    {
        var thread = await StartGame(0);
        await Say(Alice, "g3", thread);
        await Mention(Bob, "resign", thread);

        var pgn = Assert.Single(_analysis.Imported);
        Assert.Contains("[Variant \"Chess960\"]", pgn);
        Assert.Contains("[FEN \"bbqnnrkr/pppppppp/8/8/8/8/PPPPPPPP/BBQNNRKR w HFhf - 0 1\"]", pgn);
        Assert.Contains("Chess960", _slack.Updated[^1].Text);

        await Mention(Alice, "leaderboard 960");
        Assert.Contains("Chess960 Leaderboard", _slack.Posted[^1].Text);
        Assert.Contains($"<@{Alice}> — *1216*", _slack.Posted[^1].Text);

        await Mention(Alice, "leaderboard");
        Assert.Contains("No games have been finished yet", _slack.Posted[^1].Text);

        await Mention(Alice, "stats");
        Assert.Contains("Chess960: *1* wins", _slack.Posted[^1].Text);
    }

    private async Task<string> StartGame(int number)
    {
        await Mention(Alice, $"chess960 <@{Bob}> white");
        var challenged = (await _games.GetOpenGamesAsync(Bob)).Single(g => g.State == GameState.Challenged);

        // Swap in a known start so the test can play real moves; the challenge picked one at random.
        var record = new GameRecord
        {
            ChannelId = challenged.ChannelId, ThreadTs = challenged.ThreadTs, Mode = GameMode.Chess960, Chess960Number = number,
            ChallengerId = Alice, OpponentId = Bob, WhiteId = Alice, BlackId = Bob,
            CreatedAt = challenged.CreatedAt, UpdatedAt = challenged.UpdatedAt,
        };
        await _games.SaveAsync(record);
        await Click(Bob, ActionIds.Accept, record.ThreadTs);
        return record.ThreadTs;
    }

    private Task Mention(string user, string text, string? thread = null) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, $"<@{FakeSlackApi.BotId}> {text}", NextTs(), thread, IsMention: true));

    private Task Say(string user, string text, string thread) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, text, NextTs(), thread, IsMention: false));

    private Task Click(string user, string actionId, string thread) =>
        _bot.HandleActionAsync(new IncomingAction(actionId, user, Channel, thread));

    private string NextTs() => $"{_ts++}.000001";
}

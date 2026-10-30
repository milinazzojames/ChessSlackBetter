using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Bot.Users;
using ChessSlackBetter.Chess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Tests;

public class ChessBotServiceTests
{
    private const string Channel = "C1";
    private const string Alice = "UALICE";
    private const string Bob = "UBOB";
    private const string Carol = "UCAROL";

    private readonly FakeSlackApi _slack = new();
    private readonly InMemoryGameRepository _games = new();
    private readonly InMemoryUserRepository _users = new();
    private readonly FakeAnalysisLinks _analysis = new();
    private readonly ChessBotService _bot;
    private int _ts = 1;

    public ChessBotServiceTests()
    {
        var options = Options.Create(new ChessBotOptions { PublicBaseUrl = "https://chess.example.com" });
        _bot = new ChessBotService(_slack, _games, _users, _analysis, new FakeDiceJudge(), new FakeDiceRoller(), options, TimeProvider.System, NullLogger<ChessBotService>.Instance);
    }

    [Fact]
    public async Task Challenge_posts_a_card_with_its_own_thread()
    {
        await Mention(Alice, $"challenge <@{Bob}> white");

        var card = Assert.Single(_slack.Posted);
        Assert.Null(card.ThreadTs);
        Assert.Contains($"<@{Alice}> has challenged <@{Bob}>", card.Text);

        var record = await _games.GetAsync(Channel, "1000.000100");
        Assert.NotNull(record);
        Assert.Equal(GameState.Challenged, record.State);
        Assert.Equal(Alice, record.WhiteId);
        Assert.Equal(Bob, record.BlackId);
    }

    [Fact]
    public async Task Cannot_challenge_yourself_or_the_bot()
    {
        await Mention(Alice, $"challenge <@{Alice}>");
        await Mention(Alice, $"challenge <@{FakeSlackApi.BotId}>");
        Assert.Empty(_slack.Posted);
        Assert.Equal(2, _slack.Ephemeral.Count);
    }

    [Fact]
    public async Task Only_the_challenged_player_can_accept()
    {
        var thread = await StartChallenge();
        await Click(Carol, ActionIds.Accept, thread);
        await Click(Alice, ActionIds.Accept, thread);
        Assert.Equal(GameState.Challenged, (await _games.GetAsync(Channel, thread))!.State);
        Assert.Equal(2, _slack.Ephemeral.Count);

        await Click(Bob, ActionIds.Accept, thread);
        Assert.Equal(GameState.Active, (await _games.GetAsync(Channel, thread))!.State);
        Assert.Contains("game in progress", _slack.Updated.Single().Text);

        var board = _slack.Posted[^1];
        Assert.Equal(thread, board.ThreadTs);
        Assert.Contains($"<@{Alice}>, it's your move (white)", board.Text);
        Assert.Contains("board.png?fen=", board.Blocks!.ToJsonString());
    }

    [Fact]
    public async Task Board_uses_the_preferences_of_the_player_to_move()
    {
        await Mention(Bob, "theme Blue");
        await Mention(Bob, "pieces fantasy");
        Assert.Equal(new UserPreferences("blue", "fantasy"), _users.Preferences[Bob]);
        await Mention(Bob, "theme plaid");
        Assert.Contains("no `plaid` board theme", _slack.Ephemeral[^1].Text);
        await Mention(Bob, "pieces");
        Assert.Contains("Your piece set is *fantasy*", _slack.Ephemeral[^1].Text);

        var thread = await StartChallenge();
        await Click(Bob, ActionIds.Accept, thread);
        Assert.DoesNotContain("theme=", _slack.Posted[^1].Blocks!.ToJsonString());

        await Mention(Alice, "e4", thread);
        var url = _slack.Posted[^1].Blocks!.ToJsonString();
        Assert.Contains("theme=blue", url);
        Assert.Contains("pieces=fantasy", url);
    }

    [Fact]
    public async Task Declining_closes_the_challenge()
    {
        var thread = await StartChallenge();
        await Mention(Bob, "decline", thread);
        Assert.Equal(GameState.Declined, (await _games.GetAsync(Channel, thread))!.State);
        Assert.Contains("declined", _slack.Updated.Single().Text);
    }

    [Fact]
    public async Task Players_move_in_turn_with_or_without_a_mention()
    {
        var thread = await StartGame();

        await Mention(Alice, "e2e4", thread);
        await Say(Bob, "e5", thread);
        await Say(Bob, "Nc6", thread);
        await Say(Alice, "nice", thread);
        await Say(Carol, "Nf3", thread);

        var record = (await _games.GetAsync(Channel, thread))!;
        Assert.Equal(["e2e4", "e7e5"], record.Moves);
        Assert.Contains(_slack.Ephemeral, e => e.User == Bob && e.Text.Contains("not your turn"));
        Assert.Contains("Last move: `1... e5`", _slack.Posted[^1].Blocks![0]!["text"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Illegal_moves_are_rejected_privately()
    {
        var thread = await StartGame();
        var posted = _slack.Posted.Count;

        await Mention(Alice, "e2e5", thread);

        Assert.Equal(posted, _slack.Posted.Count);
        var error = Assert.Single(_slack.Ephemeral);
        Assert.Equal(Alice, error.User);
        Assert.Contains("isn't a legal move", error.Text);
    }

    [Fact]
    public async Task Checkmate_finishes_the_game_and_broadcasts_the_result()
    {
        var thread = await StartGame();
        foreach (var (player, move) in new[] { (Alice, "f3"), (Bob, "e5"), (Alice, "g4"), (Bob, "Qh4#") })
        {
            await Say(player, move, thread);
        }

        var record = (await _games.GetAsync(Channel, thread))!;
        Assert.Equal(GameState.Finished, record.State);
        Assert.Equal(GameOutcome.BlackWins, record.Outcome);
        Assert.Equal("https://lichess.org/abc123", record.AnalysisUrl);

        var gameOver = _slack.Posted[^1];
        Assert.True(gameOver.Broadcast);
        Assert.Contains("Checkmate!", gameOver.Text);
        Assert.Contains("1. f3 e5 2. g4 Qh4#", gameOver.Blocks!.ToJsonString());
        Assert.Contains("[White \\u0022ualice\\u0022]", gameOver.Blocks.ToJsonString());
        Assert.Contains("0-1", _slack.Updated[^1].Text);

        await Say(Alice, "e4", thread);
        Assert.Equal(4, (await _games.GetAsync(Channel, thread))!.Moves.Count);
    }

    [Fact]
    public async Task Resign_button_ends_the_game()
    {
        var thread = await StartGame();
        await Click(Alice, ActionIds.Resign, thread);

        var record = (await _games.GetAsync(Channel, thread))!;
        Assert.Equal(GameOutcome.BlackWins, record.Outcome);
        Assert.Equal(Termination.Resignation, record.Termination);
        Assert.Contains($"<@{Alice}> resigned", _slack.Posted[^1].Text);
    }

    [Fact]
    public async Task Draw_needs_both_players()
    {
        var thread = await StartGame();
        await Mention(Alice, "draw", thread);
        Assert.Contains("offers a draw", _slack.Posted[^1].Text);
        Assert.Equal(GameState.Active, (await _games.GetAsync(Channel, thread))!.State);

        await Click(Bob, ActionIds.AcceptDraw, thread);
        var record = (await _games.GetAsync(Channel, thread))!;
        Assert.Equal(GameOutcome.Draw, record.Outcome);
        Assert.Equal(Termination.DrawAgreement, record.Termination);
    }

    [Fact]
    public async Task Draw_offer_can_be_declined_and_is_cleared_by_a_move()
    {
        var thread = await StartGame();
        await Mention(Alice, "draw", thread);
        await Click(Bob, ActionIds.DeclineDraw, thread);
        Assert.Null((await _games.GetAsync(Channel, thread))!.DrawOfferedBy);

        await Mention(Alice, "draw", thread);
        await Say(Alice, "e4", thread);
        Assert.Null((await _games.GetAsync(Channel, thread))!.DrawOfferedBy);
    }

    [Fact]
    public async Task Abort_only_before_both_players_move_and_does_not_count()
    {
        var thread = await StartGame();
        await Say(Alice, "e4", thread);
        await Mention(Bob, "abort", thread);
        Assert.Equal(Termination.Abandoned, (await _games.GetAsync(Channel, thread))!.Termination);
        Assert.Empty(_analysis.Imported);

        var second = await StartGame();
        await Say(Alice, "e4", second);
        await Say(Bob, "e5", second);
        await Mention(Bob, "abort", second);
        Assert.Equal(GameState.Active, (await _games.GetAsync(Channel, second))!.State);

        await Mention(Alice, "stats");
        Assert.Contains("hasn't finished any games", _slack.Posted[^1].Text);
    }

    [Fact]
    public async Task Stats_and_leaderboard_count_results()
    {
        var first = await StartGame();
        await Click(Bob, ActionIds.Resign, first);
        var second = await StartGame();
        await Mention(Alice, "draw", second);
        await Mention(Bob, "draw", second);

        await Mention(Carol, $"stats <@{Alice}>");
        Assert.Contains("*1* wins, *0* losses, *1* draws (2 games)", _slack.Posted[^1].Text);

        await Mention(Carol, "leaderboard");
        var board = _slack.Posted[^1].Text;
        Assert.True(board.IndexOf(Alice, StringComparison.Ordinal) < board.IndexOf(Bob, StringComparison.Ordinal));
        Assert.Contains("*1.5* pts", board);
    }

    [Fact]
    public async Task Games_lists_open_games_with_links()
    {
        await StartGame();
        await Mention(Alice, "games");
        var reply = _slack.Ephemeral[^1].Text;
        Assert.Contains($"vs <@{Bob}>", reply);
        Assert.Contains("*your move*", reply);
        Assert.Contains("https://example.slack.com/archives/", reply);
    }

    [Fact]
    public async Task Game_commands_outside_a_game_thread_get_a_hint()
    {
        await Mention(Alice, "e4");
        Assert.Contains("no game in this thread", _slack.Ephemeral.Single().Text);
    }

    [Fact]
    public async Task Text_boards_are_used_without_a_public_url()
    {
        var bot = new ChessBotService(_slack, _games, _users, _analysis, new FakeDiceJudge(), new FakeDiceRoller(), Options.Create(new ChessBotOptions()), TimeProvider.System, NullLogger<ChessBotService>.Instance);
        await bot.HandleMessageAsync(new(Channel, Alice, $"<@{FakeSlackApi.BotId}> challenge <@{Bob}> white", NextTs(), null, true));
        var thread = "1000.000100";
        await bot.HandleActionAsync(new(ActionIds.Accept, Bob, Channel, thread));

        var blocks = _slack.Posted[^1].Blocks!.ToJsonString();
        Assert.DoesNotContain("board.png", blocks);
        Assert.Contains("r n b q k b n r", blocks);
    }

    private async Task<string> StartChallenge()
    {
        await Mention(Alice, $"challenge <@{Bob}> white");
        var record = (await _games.GetOpenGamesAsync(Bob)).Single(g => g.State == GameState.Challenged);
        return record.ThreadTs;
    }

    private async Task<string> StartGame()
    {
        var thread = await StartChallenge();
        await Click(Bob, ActionIds.Accept, thread);
        return thread;
    }

    private Task Mention(string user, string text, string? thread = null) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, $"<@{FakeSlackApi.BotId}> {text}", NextTs(), thread, IsMention: true));

    private Task Say(string user, string text, string thread) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, text, NextTs(), thread, IsMention: false));

    private Task Click(string user, string actionId, string thread) =>
        _bot.HandleActionAsync(new IncomingAction(actionId, user, Channel, thread));

    private string NextTs() => $"{_ts++}.000001";
}

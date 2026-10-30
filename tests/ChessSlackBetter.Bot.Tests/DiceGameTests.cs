using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Chess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Tests;

public class DiceGameTests
{
    private const string Channel = "C1";
    private const string Alice = "UALICE";
    private const string Bob = "UBOB";
    private const string Gamble = "turn my e4 pawn into a bishop, if I fail I lose the pawn";

    private readonly FakeSlackApi _slack = new();
    private readonly InMemoryGameRepository _games = new();
    private readonly InMemoryUserRepository _users = new();
    private readonly FakeDiceJudge _judge = new();
    private readonly FakeDiceRoller _roller = new();
    private readonly ChessBotService _bot;
    private int _ts = 1;

    public DiceGameTests()
    {
        var options = Options.Create(new ChessBotOptions { PublicBaseUrl = "https://chess.example.com" });
        _bot = new ChessBotService(_slack, _games, _users, new FakeAnalysisLinks(), _judge, _roller, options, TimeProvider.System,
            NullLogger<ChessBotService>.Instance);
    }

    [Fact]
    public async Task Diceroll_challenge_starts_a_dice_game()
    {
        await Mention(Alice, $"diceroll <@{Bob}> white");
        Assert.Contains("ChessDiceRoll", _slack.Posted.Single().Text);

        var thread = (await _games.GetOpenGamesAsync(Bob)).Single().ThreadTs;
        await Click(Bob, ActionIds.Accept, thread);

        Assert.Equal(GameMode.DiceRoll, (await Record(thread)).Mode);
        Assert.Contains("Rolls left", _slack.Posted[^1].Blocks!.ToJsonString());
    }

    [Fact]
    public async Task A_gamble_is_priced_fairly_and_does_not_use_the_turn_yet()
    {
        var thread = await StartAfterE4E5();

        await Mention(Alice, Gamble, thread);

        Assert.Equal(Gamble, _judge.Requests.Single());
        var pending = (await Record(thread)).Dice.Pending;
        Assert.NotNull(pending);

        // Success gains 2 (pawn -> bishop), failure loses 1: fair odds are 1/3, so 14+ on a d20.
        Assert.Equal(14, pending.Target);
        Assert.Contains("14+", _slack.Posted[^1].Text);
        Assert.Contains("35% chance", _slack.Posted[^1].Blocks!.ToJsonString());
        Assert.Equal(PieceColor.White, (await Record(thread)).LoadGame().Current.SideToMove);
    }

    [Fact]
    public async Task Winning_the_roll_applies_the_reward_and_passes_the_turn()
    {
        var thread = await StartAfterE4E5();
        await Mention(Alice, Gamble, thread);
        _roller.Next = 17;

        await Click(Alice, ActionIds.Roll, thread);

        var record = await Record(thread);
        var position = record.LoadGame().Current;
        Assert.Equal(new Piece(PieceType.Bishop, PieceColor.White), position[Square.Parse("e4")]);
        Assert.Equal(PieceColor.Black, position.SideToMove);
        Assert.Null(record.Dice.Pending);
        Assert.Equal(2, record.Dice.RollsLeft(Alice));
        Assert.True(record.Dice.Log.Single().Success);
        Assert.Contains("Rolled *17*", _slack.Updated[^1].Text);
        Assert.Contains("hl=e4", _slack.Posted[^1].Blocks!.ToJsonString());
    }

    [Fact]
    public async Task Losing_the_roll_applies_the_penalty()
    {
        var thread = await StartAfterE4E5();
        await Mention(Alice, Gamble, thread);
        _roller.Next = 13;

        await Say(Alice, "yes", thread);

        var record = await Record(thread);
        Assert.True(record.LoadGame().Current[Square.Parse("e4")].IsEmpty);
        Assert.False(record.Dice.Log.Single().Success);
        Assert.Contains("*Failed.*", _slack.Updated[^1].Text);
    }

    [Fact]
    public async Task Only_the_requester_can_roll_or_cancel()
    {
        var thread = await StartAfterE4E5();
        await Mention(Alice, Gamble, thread);

        await Click(Bob, ActionIds.Roll, thread);
        await Click(Bob, ActionIds.CancelRoll, thread);
        await Say(Bob, "yes", thread);

        Assert.NotNull((await Record(thread)).Dice.Pending);
        Assert.Equal(2, _slack.Ephemeral.Count(e => e.User == Bob));
    }

    [Fact]
    public async Task Cancelling_is_free_and_keeps_the_turn()
    {
        var thread = await StartAfterE4E5();
        await Mention(Alice, Gamble, thread);

        await Say(Alice, "no", thread);

        var record = await Record(thread);
        Assert.Null(record.Dice.Pending);
        Assert.Equal(DiceLimits.RollsPerPlayer, record.Dice.RollsLeft(Alice));
        Assert.Contains("Cancelled", _slack.Updated[^1].Text);

        await Say(Alice, "Nf3", thread);
        Assert.Equal(3, (await Record(thread)).Moves.Count);
    }

    [Fact]
    public async Task Opponent_gets_one_veto()
    {
        var thread = await StartAfterE4E5();
        await Mention(Alice, Gamble, thread);
        await Click(Bob, ActionIds.Veto, thread);

        var record = await Record(thread);
        Assert.Null(record.Dice.Pending);
        Assert.Equal(0, record.Dice.VetoesLeft(Bob));
        Assert.Equal(DiceLimits.RollsPerPlayer, record.Dice.RollsLeft(Alice));
        Assert.Contains("Vetoed", _slack.Updated[^1].Text);

        await Mention(Alice, Gamble, thread);
        Assert.DoesNotContain("chess_veto", _slack.Posted[^1].Blocks!.ToJsonString());
        await Mention(Bob, "veto", thread);
        Assert.NotNull((await Record(thread)).Dice.Pending);
        Assert.Contains("already used your veto", _slack.Ephemeral[^1].Text);
    }

    [Fact]
    public async Task Moving_instead_withdraws_the_gamble()
    {
        var thread = await StartAfterE4E5();
        await Mention(Alice, Gamble, thread);

        await Say(Alice, "Nf3", thread);

        Assert.Null((await Record(thread)).Dice.Pending);
        Assert.Contains("Withdrawn", _slack.Updated[^1].Text);
    }

    [Fact]
    public async Task Each_player_gets_three_rolls()
    {
        var thread = await StartAfterE4E5();
        string[] squares = ["a2", "b2", "c2"];
        string[] blackMoves = ["Nf6", "Ng8", "Nf6"];
        for (var i = 0; i < 3; i++)
        {
            _judge.Ruling = FakeDiceJudge.Accept(
                [new DiceEffect("transform", squares[i], "", "knight", "")],
                [new DiceEffect("remove", squares[i], "", "", "")]);
            await Mention(Alice, "gamble", thread);
            await Click(Alice, ActionIds.Roll, thread);
            await Say(Bob, blackMoves[i], thread);
        }

        await Mention(Alice, "one more please", thread);

        Assert.Equal(0, (await Record(thread)).Dice.RollsLeft(Alice));
        Assert.Null((await Record(thread)).Dice.Pending);
        Assert.Contains("used all 3", _slack.Ephemeral[^1].Text);
    }

    [Theory]
    [InlineData("remove", "e4", "remove", "d2", "wouldn't actually help")]
    [InlineData("transform", "e4", "transform", "d2", "has to cost you")]
    [InlineData("remove", "e8", "remove", "e4", "Kings")]
    public async Task Unfair_or_illegal_gambles_are_refused(string successType, string successSquare, string failureType, string failureSquare, string expected)
    {
        var thread = await StartAfterE4E5();
        _judge.Ruling = FakeDiceJudge.Accept(
            [new DiceEffect(successType, successSquare, "", successType == "transform" ? "queen" : "", "")],
            [new DiceEffect(failureType, failureSquare, "", failureType == "transform" ? "queen" : "", "")]);

        await Mention(Alice, "something odd", thread);

        Assert.Null((await Record(thread)).Dice.Pending);
        Assert.Contains(expected, _slack.Ephemeral[^1].Text);
    }

    [Fact]
    public async Task Referee_rejections_are_passed_on()
    {
        var thread = await StartAfterE4E5();
        _judge.Ruling = new DiceRuling(false, "What happens if you fail?", [], [], "", "", 0, 0, "");

        await Mention(Alice, "make my pawn a queen", thread);

        Assert.Contains("What happens if you fail?", _slack.Ephemeral[^1].Text);
    }

    [Fact]
    public async Task Gambles_only_on_your_turn_and_only_in_dice_games()
    {
        var thread = await StartAfterE4E5();
        await Mention(Bob, Gamble, thread);
        Assert.Contains("your own turn", _slack.Ephemeral[^1].Text);

        await Mention(Alice, $"challenge <@{Bob}> white");
        var standard = (await _games.GetOpenGamesAsync(Bob)).Single(g => g.Mode == GameMode.Standard).ThreadTs;
        await Click(Bob, ActionIds.Accept, standard);
        await Mention(Alice, $"roll {Gamble}", standard);
        Assert.Contains("only for ChessDiceRoll", _slack.Ephemeral[^1].Text);
        Assert.Empty(_judge.Requests);
    }

    [Fact]
    public async Task Dice_stats_are_kept_separately()
    {
        var thread = await StartAfterE4E5();
        await Mention(Alice, Gamble, thread);
        await Click(Alice, ActionIds.Roll, thread);
        await Click(Bob, ActionIds.Resign, thread);

        await Mention(Alice, "stats");
        var stats = _slack.Posted[^1].Text;
        Assert.Contains("ChessDiceRoll: *1* wins", stats);
        Assert.Contains("Gambles: *1* of 1 won", stats);
        Assert.DoesNotContain("Chess: ", stats.Replace("ChessDiceRoll: ", ""));

        await Mention(Alice, "leaderboard");
        Assert.Contains("No games have been finished", _slack.Posted[^1].Text);
        await Mention(Alice, "leaderboard dice");
        Assert.Contains("gambles won 1/1", _slack.Posted[^1].Text);
    }

    private async Task<string> StartAfterE4E5()
    {
        await Mention(Alice, $"diceroll <@{Bob}> white");
        var thread = (await _games.GetOpenGamesAsync(Bob)).Single(g => g.State == GameState.Challenged).ThreadTs;
        await Click(Bob, ActionIds.Accept, thread);
        await Say(Alice, "e4", thread);
        await Say(Bob, "e5", thread);
        return thread;
    }

    private async Task<GameRecord> Record(string thread) => (await _games.GetAsync(Channel, thread))!;

    private Task Mention(string user, string text, string? thread = null) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, $"<@{FakeSlackApi.BotId}> {text}", NextTs(), thread, IsMention: true));

    private Task Say(string user, string text, string thread) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, text, NextTs(), thread, IsMention: false));

    private Task Click(string user, string actionId, string thread) =>
        _bot.HandleActionAsync(new IncomingAction(actionId, user, Channel, thread));

    private string NextTs() => $"{_ts++}.000001";
}

public class DiceRulesTests
{
    [Theory]
    [InlineData(2, 1, 14)]
    [InlineData(1, 1, 11)]
    [InlineData(8, 1, 19)]
    [InlineData(1, 8, 3)]
    [InlineData(100, 1, 20)]
    [InlineData(1, 100, 2)]
    public void Fair_target_balances_reward_and_risk(double gain, double loss, int expected)
    {
        Assert.Equal(expected, DiceRules.FairTarget(gain, loss));
    }

    [Fact]
    public void Builds_edits_and_measures_material()
    {
        var position = Position.Start;
        Assert.True(DiceRules.TryBuildEdits(position,
        [
            new DiceEffect("move", "b1", "c3", "", ""),
            new DiceEffect("transform", "d8", "", "pawn", "white"),
        ], out var edits, out var error), error);

        Assert.Equal(["b1=-", "c3=N", "d8=P"], edits.Select(e => e.ToString()).Order());

        // Stealing the black queen as a white pawn: +9 (their queen gone) +1 (our new pawn).
        Assert.Equal(10, DiceRules.MaterialDelta(position, edits, PieceColor.White));
        Assert.Equal(-10, DiceRules.MaterialDelta(position, edits, PieceColor.Black));
    }

    [Theory]
    [InlineData("transform", "e4", "queen", "There's no piece on e4")]
    [InlineData("add", "e2", "queen", "already occupied")]
    [InlineData("remove", "z9", "", "isn't a square")]
    [InlineData("explode", "e2", "", "Unknown effect")]
    public void Rejects_impossible_effects(string type, string square, string piece, string expected)
    {
        Assert.False(DiceRules.TryBuildEdits(Position.Start, [new DiceEffect(type, square, "", piece, "white")], out _, out var error));
        Assert.Contains(expected, error);
    }
}

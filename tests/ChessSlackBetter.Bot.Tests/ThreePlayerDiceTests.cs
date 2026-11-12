using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Chess;
using ChessSlackBetter.Chess.ThreePlayer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Tests;

public class ThreePlayerDiceTests
{
    private const string Channel = "C1";
    private const string Alice = "UALICE";
    private const string Bob = "UBOB";
    private const string Carol = "UCAROL";

    private readonly FakeSlackApi _slack = new();
    private readonly InMemoryThreePlayerRepository _games = new();
    private readonly FakeDiceJudge _judge = new();
    private readonly FakeDiceRoller _roller = new();
    private readonly ChessBotService _bot;
    private int _ts = 1;

    public ThreePlayerDiceTests()
    {
        var options = Options.Create(new ChessBotOptions { PublicBaseUrl = "https://chess.example.com" });
        _bot = new ChessBotService(_slack, new InMemoryGameRepository(), new InMemoryUserRepository(), _games, new FakeAnalysisLinks(), _judge, _roller, new FakePhilosopher(),
            options, TimeProvider.System, NullLogger<ChessBotService>.Instance);

        // White gambles to turn Black's queen into a pawn, risking their own a1 rook.
        _judge.Ruling = FakeDiceJudge.Accept(
            [new DiceEffect("transform", "i8", "", "pawn", "")],
            [new DiceEffect("remove", "a1", "", "", "")]);
    }

    [Fact]
    public async Task Damage_to_one_opponent_counts_half_in_the_odds()
    {
        var (thread, white, black, red) = await StartAsync();

        await Mention(white, "turn black's queen into a pawn, if I fail I lose my a1 rook", thread);

        // Black loses 8, which is worth 8 / 2 = 4 to white; failing costs white 5. Fair odds 5/9, so 45+ on a d100.
        var pending = (await Record(thread)).Dice.Pending!;
        Assert.Equal(45, pending.Target);
        Assert.Contains($"<@{black}> or <@{red}> can veto this", _slack.Posted[^1].Blocks!.ToJsonString().Replace("\\u003C", "<").Replace("\\u003E", ">"));
        Assert.Equal(TriColor.White, (await Record(thread)).LoadGame().Current.SideToMove);
    }

    [Fact]
    public async Task Winning_the_roll_changes_the_board_and_passes_the_turn()
    {
        var (thread, white, _, _) = await StartAsync();
        await Mention(white, "gamble", thread);
        _roller.Next = 70;

        await Say(white, "yes", thread);

        var record = await Record(thread);
        var position = record.LoadGame().Current;
        Assert.Equal(new TriPiece(PieceType.Pawn, TriColor.Black), position[TriSquare.Parse("i8")]);
        Assert.Equal(TriColor.Black, position.SideToMove);
        Assert.Equal(DiceLimits.RollsPerPlayer - 1, record.Dice.RollsLeft(white));
        Assert.Contains("hl=i8", _slack.Posted[^1].Blocks!.ToJsonString());
        Assert.Contains("Rolls left", _slack.Posted[^1].Blocks!.ToJsonString());
    }

    [Fact]
    public async Task Either_opponent_can_veto_once()
    {
        var (thread, white, black, red) = await StartAsync();

        await Mention(white, "gamble", thread);
        await Click(black, ActionIds.Veto, thread);
        Assert.Null((await Record(thread)).Dice.Pending);

        await Mention(white, "gamble again", thread);
        await Say(black, "veto", thread);
        Assert.NotNull((await Record(thread)).Dice.Pending);
        Assert.Contains("already used your veto", _slack.Replies[^1].Text);

        await Say(red, "veto", thread);
        var record = await Record(thread);
        Assert.Null(record.Dice.Pending);
        Assert.Equal(DiceLimits.RollsPerPlayer, record.Dice.RollsLeft(white));
        Assert.Equal(TriColor.White, record.LoadGame().Current.SideToMove);
    }

    [Fact]
    public async Task Moving_withdraws_a_gamble_and_plain_games_refuse_them()
    {
        var (thread, white, _, _) = await StartAsync();
        await Mention(white, "gamble", thread);

        await Say(white, "d4", thread);
        Assert.Null((await Record(thread)).Dice.Pending);
        Assert.Contains("Withdrawn", _slack.Updated[^1].Text);

        await Mention(Alice, $"threeplayerchess <@{Bob}> <@{Carol}>");
        var plain = (await _games.GetOpenGamesAsync(Bob)).Single(g => g.State == GameState.Challenged);
        await Click(Bob, ActionIds.Accept, plain.ThreadTs);
        await Click(Carol, ActionIds.Accept, plain.ThreadTs);
        await Mention(plain.WhiteId, "roll turn their queen into a pawn, if I fail I lose a rook", plain.ThreadTs);
        Assert.Contains("only for dice games", _slack.Replies[^1].Text);
    }

    [Fact]
    public async Task Dice_games_have_their_own_leaderboard()
    {
        var (thread, white, black, red) = await StartAsync();
        await Mention(white, "gamble", thread);
        await Click(white, ActionIds.Roll, thread);
        await Mention(red, "resign", thread);
        await Mention(black, "resign", thread);

        await Mention(Alice, "leaderboard 3pdice");
        Assert.Contains($"1. <@{white}> — *1* pts", _slack.Posted[^1].Text);
        Assert.Contains("gambles won 1/1", _slack.Posted[^1].Text);

        await Mention(Alice, "leaderboard 3p");
        Assert.Contains("No Three-Player Chess games", _slack.Posted[^1].Text);
    }

    private async Task<(string Thread, string White, string Black, string Red)> StartAsync()
    {
        await Mention(Alice, $"threeplayerdice <@{Bob}> <@{Carol}>");
        var thread = (await _games.GetOpenGamesAsync(Bob)).Single(g => g.State == GameState.Challenged).ThreadTs;
        Assert.Contains("Three-Player ChessDiceRoll", _slack.Posted[^1].Text);
        await Click(Bob, ActionIds.Accept, thread);
        await Click(Carol, ActionIds.Accept, thread);
        var record = await Record(thread);
        Assert.True(record.WithDice);
        return (thread, record.WhiteId, record.BlackId, record.RedId);
    }

    private async Task<ThreePlayerRecord> Record(string thread) => (await _games.GetAsync(Channel, thread))!;

    private Task Mention(string user, string text, string? thread = null) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, $"<@{FakeSlackApi.BotId}> {text}", NextTs(), thread, IsMention: true));

    private Task Say(string user, string text, string thread) =>
        _bot.HandleMessageAsync(new IncomingMessage(Channel, user, text, NextTs(), thread, IsMention: false));

    private Task Click(string user, string actionId, string thread) =>
        _bot.HandleActionAsync(new IncomingAction(actionId, user, Channel, thread));

    private string NextTs() => $"{_ts++}.000001";
}

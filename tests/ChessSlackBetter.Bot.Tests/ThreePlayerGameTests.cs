using System.Text.Encodings.Web;
using System.Text.Json;
using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Chess.ThreePlayer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Tests;

public class ThreePlayerGameTests
{
    private const string Channel = "C1";
    private const string Alice = "UALICE";
    private const string Bob = "UBOB";
    private const string Carol = "UCAROL";
    private const string Dave = "UDAVE";

    private readonly FakeSlackApi _slack = new();
    private readonly InMemoryThreePlayerRepository _games = new();
    private readonly ChessBotService _bot;
    private int _ts = 1;

    public ThreePlayerGameTests()
    {
        var options = Options.Create(new ChessBotOptions { PublicBaseUrl = "https://chess.example.com" });
        _bot = new ChessBotService(_slack, new InMemoryGameRepository(), new InMemoryUserRepository(), _games, new FakeAnalysisLinks(), new FakeDiceJudge(),
            new FakeDiceRoller(), new FakePhilosopher(), options, TimeProvider.System, NullLogger<ChessBotService>.Instance);
    }

    [Fact]
    public async Task Everyone_has_to_accept_before_the_game_starts()
    {
        await Mention(Alice, $"threeplayerchess <@{Bob}> <@{Carol}>");
        Assert.Contains("Three-Player Chess", _slack.Posted.Single().Text);
        var thread = (await _games.GetOpenGamesAsync(Carol)).Single().ThreadTs;

        await Click(Bob, ActionIds.Accept, thread);
        Assert.Equal(GameState.Challenged, (await Record(thread)).State);
        var card = _slack.Updated[^1].Blocks!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains($"Waiting for <@{Carol}> to accept", card);

        await Click(Dave, ActionIds.Accept, thread);
        Assert.Contains("isn't for you", _slack.Replies[^1].Text);

        await Mention(Carol, "accept", thread);
        var record = await Record(thread);
        Assert.Equal(GameState.Active, record.State);
        Assert.Equal([Alice, Bob, Carol], record.Players.Order());
        Assert.Contains("board3.png", _slack.Posted[^1].Blocks!.ToJsonString());
        Assert.Contains($"<@{record.WhiteId}>, it's your move (white)", _slack.Posted[^1].Text);
    }

    [Fact]
    public async Task Players_take_turns_and_can_just_type_their_moves()
    {
        var (thread, white, black, red) = await StartAsync();

        await Say(black, "i5", thread);
        Assert.Contains("not your turn", _slack.Replies[^1].Text);

        await Say(white, "d4", thread);
        await Say(Dave, "i5", thread);
        await Say(black, "i7i5", thread);
        await Mention(red, "Nf10", thread);

        var record = await Record(thread);
        Assert.Equal(["d2d4", "i7i5", "g12f10"], record.Moves);
        Assert.Equal(TriColor.White, record.LoadGame().Current.SideToMove);
        Assert.Contains("Round 2", _slack.Posted[^1].Blocks!.ToJsonString());
        Assert.Contains("hl=g12,f10", _slack.Posted[^1].Blocks!.ToJsonString());
    }

    [Fact]
    public async Task Resigning_knocks_a_player_out_and_the_last_one_standing_wins()
    {
        var (thread, white, black, red) = await StartAsync();

        await Click(red, ActionIds.Resign, thread);
        Assert.Contains(_slack.Posted, m => m.Text.Contains($"<@{red}> (red) resigned and is out"));
        Assert.Equal(GameState.Active, (await Record(thread)).State);

        await Say(red, "e10", thread);
        Assert.Contains("You're out", _slack.Replies[^1].Text);

        await Say(white, "d4", thread);
        await Say(black, "i5", thread);
        Assert.Equal(TriColor.White, (await Record(thread)).LoadGame().Current.SideToMove);

        await Mention(black, "resign", thread);

        var record = await Record(thread);
        Assert.Equal(GameState.Finished, record.State);
        var over = _slack.Posted.Last(m => m.Broadcast);
        Assert.Contains($"<@{white}> is the last player standing and wins!", over.Text);
        Assert.Contains($":first_place_medal: <@{white}>", _slack.Updated[^1].Text);

        await Mention(Dave, "leaderboard 3p");
        Assert.Contains($"1. <@{white}> — *1* pts (1× 1st, 0× 2nd, 0× 3rd)", _slack.Posted[^1].Text);
        Assert.Contains($"3. <@{red}> — *0* pts", _slack.Posted[^1].Text);

        await Mention(black, "stats");
        Assert.Contains("Three-Player Chess: 0× 1st, 1× 2nd, 0× 3rd (1 games)", _slack.Posted[^1].Text);
    }

    [Fact]
    public async Task A_draw_needs_everyone_still_playing_to_agree()
    {
        var (thread, white, black, red) = await StartAsync();

        await Click(white, ActionIds.OfferDraw, thread);
        Assert.Contains($"<@{black}> and <@{red}>, do you agree?", _slack.Posted[^1].Text);

        await Click(black, ActionIds.AcceptDraw, thread);
        Assert.Equal(GameState.Active, (await Record(thread)).State);

        await Click(red, ActionIds.AcceptDraw, thread);
        Assert.Equal(GameState.Finished, (await Record(thread)).State);
        Assert.Contains("Draw agreed", _slack.Updated[^1].Text);
    }

    [Fact]
    public async Task A_draw_offer_lapses_when_someone_declines_or_moves()
    {
        var (thread, white, black, red) = await StartAsync();

        await Mention(black, "draw", thread);
        await Mention(red, "decline draw", thread);
        Assert.Empty((await Record(thread)).DrawAgreed);

        await Mention(black, "draw", thread);
        await Say(white, "d4", thread);
        Assert.Empty((await Record(thread)).DrawAgreed);
    }

    [Fact]
    public async Task Declining_cancels_the_challenge()
    {
        await Mention(Alice, $"threeplayerchess <@{Bob}> <@{Carol}>");
        var thread = (await _games.GetOpenGamesAsync(Bob)).Single().ThreadTs;

        await Click(Carol, ActionIds.Decline, thread);

        Assert.Equal(GameState.Declined, (await Record(thread)).State);
        Assert.Contains($"<@{Carol}> declined", _slack.Updated[^1].Text);
    }

    [Fact]
    public async Task Challenges_need_two_other_people()
    {
        await Mention(Alice, $"threeplayerchess <@{Bob}>");
        Assert.Contains("don't understand", _slack.Replies[^1].Text);

        await Mention(Alice, $"3p <@{Bob}> <@{Alice}>");
        Assert.Contains("you're already in", _slack.Replies[^1].Text);
        Assert.Empty(await _games.GetOpenGamesAsync(Alice));
    }

    [Fact]
    public async Task Open_games_and_gambles()
    {
        var (thread, white, _, _) = await StartAsync();

        await Mention(white, "games");
        Assert.Contains("3-player vs", _slack.Replies[^1].Text);
        Assert.Contains("*your move*", _slack.Replies[^1].Text);

        await Mention(white, "roll turn my pawn into a queen, if I fail I lose a rook", thread);
        Assert.Contains("only for dice games", _slack.Replies[^1].Text);
    }

    [Fact]
    public async Task Aborting_is_only_allowed_before_everyone_has_moved()
    {
        var (thread, white, black, red) = await StartAsync();
        await Say(white, "d4", thread);
        await Say(black, "i5", thread);
        await Say(red, "e10", thread);

        await Mention(white, "abort", thread);
        Assert.Contains("can't be aborted", _slack.Replies[^1].Text);

        var (other, otherWhite, _, _) = await StartAsync();
        await Mention(otherWhite, "abort", other);
        Assert.Contains("Game aborted", _slack.Updated[^1].Text);
    }

    private async Task<(string Thread, string White, string Black, string Red)> StartAsync()
    {
        await Mention(Alice, $"threeplayerchess <@{Bob}> <@{Carol}>");
        var thread = (await _games.GetOpenGamesAsync(Bob)).Single(g => g.State == GameState.Challenged).ThreadTs;
        await Click(Bob, ActionIds.Accept, thread);
        await Click(Carol, ActionIds.Accept, thread);
        var record = await Record(thread);
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

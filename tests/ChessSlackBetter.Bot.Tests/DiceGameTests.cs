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
        _bot = new ChessBotService(_slack, _games, _users, new InMemoryThreePlayerRepository(), new FakeAnalysisLinks(), _judge, _roller, new FakePhilosopher(), options, TimeProvider.System,
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
        Assert.Equal(DiceLimits.RollsPerPlayer - 1, record.Dice.RollsLeft(Alice));
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
    public async Task Each_player_gets_five_rolls()
    {
        var thread = await StartAfterE4E5();
        string[] squares = ["a2", "b2", "c2", "g2", "h2"];
        string[] blackMoves = ["Nf6", "Ng8", "Nf6", "Ng8", "Nf6"];
        for (var i = 0; i < 5; i++)
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
        Assert.Contains("used all 5", _slack.Ephemeral[^1].Text);
    }

    [Fact]
    public async Task Pointless_gambles_are_allowed_with_easy_odds()
    {
        var thread = await StartAfterE4E5();
        _judge.Ruling = FakeDiceJudge.Accept([], [new DiceEffect("remove", "a1", "", "", "")]);

        await Mention(Alice, "d2 pawn to d2 else I lose my a1 rook", thread);
        Assert.Equal(2, (await Record(thread)).Dice.Pending!.Target);

        await Click(Alice, ActionIds.Roll, thread);
        var record = await Record(thread);
        Assert.Equal(Position.Start[Square.Parse("a1")], record.LoadGame().Current[Square.Parse("a1")]);
        Assert.Equal(PieceColor.Black, record.LoadGame().Current.SideToMove);
        Assert.Equal(DiceLimits.RollsPerPlayer - 1, record.Dice.RollsLeft(Alice));
    }

    [Fact]
    public async Task Random_gambles_are_priced_on_average_and_picked_when_rolled()
    {
        var thread = await StartAfterE4E5();
        _judge.Ruling = FakeDiceJudge.AcceptOptions(
        [
            new DiceOption([new DiceEffect("transform", "d8", "", "pawn", "")], "Black's queen becomes a pawn"),
            new DiceOption([new DiceEffect("transform", "b8", "", "pawn", "")], "Black's b8 knight becomes a pawn"),
            // A black pawn on rank 1 is impossible, so this option is dropped from the draw.
            new DiceOption([new DiceEffect("move", "e5", "a1", "", "")], "nope"),
        ],
        [new DiceOption([new DiceEffect("remove", "g1", "", "", "")], "Your g1 knight is removed")]);
        _roller.NextPick = 1;

        await Mention(Alice, "turn a random one of their pieces into a pawn, if I fail I lose my knight", thread);

        // Gains average (8 + 2) / 2 = 5 against a loss of 3: fair odds are 3/8, so 13.5, rounded to 14+.
        var pending = (await Record(thread)).Dice.Pending!;
        Assert.Equal(2, pending.SuccessOutcomes.Count);
        Assert.Equal(14, pending.Target);

        await Click(Alice, ActionIds.Roll, thread);
        var position = (await Record(thread)).LoadGame().Current;
        Assert.Equal(new Piece(PieceType.Pawn, PieceColor.Black), position[Square.Parse("b8")]);
        Assert.Equal(new Piece(PieceType.Queen, PieceColor.Black), position[Square.Parse("d8")]);
        Assert.Contains("Black's b8 knight becomes a pawn", _slack.Posted[^2].Text);
    }

    [Theory]
    [InlineData("transform", "e4", "transform", "d2", "has to cost you")]
    [InlineData("transform", "e4", "transform", "d8", "has to cost you")]
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

    [Fact]
    public async Task A_passive_rule_fires_after_the_owners_turns_until_it_runs_out()
    {
        var thread = await StartAfterE4E5();
        var pawnRule = new DiceRuleSpec(RuleTriggers.OwnTurn, 1, 2,
            [new DiceEffect("add", DiceRules.RandomEmpty, "", "pawn", "white")], "A white pawn appears on a random empty square");
        _judge.Ruling = FakeDiceJudge.AcceptOptions(
            [new DiceOption([], "You gain a pawn on each of your next two turns", [pawnRule])],
            [new DiceOption([new DiceEffect("remove", "a2", "", "", "")], "Your a2 pawn is removed")]);

        await Mention(Alice, "every turn I gain a pawn on a random empty square, if I fail I lose a pawn", thread);

        // Two pawns from the rule against one lost: fair odds are 1/3, so 14+.
        Assert.Equal(14, (await Record(thread)).Dice.Pending!.Target);

        _roller.Next = 20;
        await Click(Alice, ActionIds.Roll, thread);
        Assert.Single((await Record(thread)).Dice.Rules);
        Assert.Contains("Rules in play", _slack.Posted[^1].Blocks!.ToJsonString());

        // Not on the turn the gamble was rolled, nor on the opponent's turn.
        await Say(Bob, "Nc6", thread);
        Assert.DoesNotContain(PlayedMove.AfterSeparator, string.Concat((await Record(thread)).Moves));

        // The fake roller always picks the first candidate off ranks 1 and 8: d2 once d4 has been played, then e2.
        await Say(Alice, "d4", thread);
        var record = await Record(thread);
        var position = record.LoadGame().Current;
        Assert.Equal(new Piece(PieceType.Pawn, PieceColor.White), position[Square.Parse("d2")]);
        Assert.Equal(PieceColor.Black, position.SideToMove);
        Assert.Equal(1, record.Dice.Rules.Single().TimesLeft);
        Assert.Contains(_slack.Posted, m => m.Text.Contains("*Rule:* A white pawn appears"));
        Assert.Contains("hl=d2", _slack.Posted[^1].Blocks!.ToJsonString());

        await Say(Bob, "Nf6", thread);
        await Say(Alice, "Nf3", thread);
        record = await Record(thread);
        Assert.Equal(new Piece(PieceType.Pawn, PieceColor.White), record.LoadGame().Current[Square.Parse("e2")]);
        Assert.Empty(record.Dice.Rules);
        Assert.Contains("d4 {rule d2=P}", record.LoadGame().MovesSan());
    }

    [Fact]
    public async Task A_riddle_rule_quizzes_the_player_to_move_before_they_can_play()
    {
        var thread = await StartAfterE4E5();
        var riddleRule = new DiceRuleSpec(RuleTriggers.EveryTurn, 1, 0,
            [new DiceEffect(DiceRules.JimmyGambit, "", "", "", DiceRules.Target)],
            "Before every turn, a riddle: right gains a pawn, wrong is a Jimmy gambit",
            RuleChallenges.Riddle,
            [new DiceEffect("add", DiceRules.RandomEmpty, "", "pawn", DiceRules.Target)],
            "");
        _judge.Ruling = FakeDiceJudge.AcceptOptions(
            [new DiceOption([], "Riddles every turn", [riddleRule])],
            [new DiceOption([new DiceEffect("remove", "a2", "", "", "")], "Your a2 pawn is removed")]);

        await Mention(Alice, "every turn a player answers a riddle, right gets a pawn else jimmy gambit, if I fail I lose a pawn", thread);
        _roller.Next = 20;
        await Click(Alice, ActionIds.Roll, thread);

        // Bob is next to move, so he's asked first and can't move until he answers.
        Assert.Contains(_slack.Posted, m => m.Text.Contains($":jigsaw: <@{Bob}>, riddle time") && m.Text.Contains("hear without ears"));
        await Say(Bob, "Nc6", thread);
        Assert.Contains("Answer your riddle first", _slack.Ephemeral[^1].Text);
        Assert.Equal(PieceColor.Black, (await Record(thread)).LoadGame().Current.SideToMove);

        // Right (plural counts too): a black pawn on the first empty square off the back ranks, e2. Still Bob's move.
        await Say(Bob, "Echoes!", thread);
        var record = await Record(thread);
        Assert.Empty(record.Dice.Riddles);
        Assert.Equal(new Piece(PieceType.Pawn, PieceColor.Black), record.LoadGame().Current[Square.Parse("e2")]);
        Assert.Equal(PieceColor.Black, record.LoadGame().Current.SideToMove);
        Assert.Contains(_slack.Posted, m => m.Text.Contains("correct!") && m.Text.Contains("*echo*"));

        // Bob moves, then Alice gets one and gets it wrong: everything but her king goes.
        await Say(Bob, "Nc6", thread);
        Assert.Equal(Alice, (await Record(thread)).Dice.Riddles.Single().SolverId);
        await Mention(Alice, "answer banana", thread);
        var position = (await Record(thread)).LoadGame().Current;
        Assert.Contains(_slack.Posted, m => m.Text.Contains("wrong!") && m.Text.Contains("The answer was *echo*"));
        Assert.Equal(1, Enumerable.Range(0, 64).Count(sq => !position[sq].IsEmpty && position[sq].Color == PieceColor.White));
        Assert.Equal(PieceColor.White, position.SideToMove);
    }

    [Fact]
    public async Task Damage_wears_pieces_down_and_follows_them_as_they_move()
    {
        var thread = await StartAfterE4E5();
        var battleship = new DiceRuleSpec(RuleTriggers.EveryTurn, 1, 0, [new DiceEffect(DiceRules.Damage, "g1", "", "", "")], "Battleship on g1");
        _judge.Ruling = FakeDiceJudge.AcceptOptions(
            [new DiceOption([], "Battleship", [battleship])],
            [new DiceOption([new DiceEffect("remove", "a2", "", "", "")], "Your a2 pawn is removed")]);

        await Mention(Alice, "battleship, if I fail I lose a pawn", thread);
        _roller.Next = 20;
        await Click(Alice, ActionIds.Roll, thread);

        await Say(Bob, "Nc6", thread);
        Assert.Equal(1, (await Record(thread)).Dice.Damage["g1"]);
        Assert.Contains(_slack.Posted, m => m.Text.Contains("g1: hit! The white knight is down to 2/3"));

        // The knight takes its damage to f3; the next shot at g1 misses.
        await Say(Alice, "Nf3", thread);
        var record = await Record(thread);
        Assert.Equal(new Dictionary<string, int> { ["f3"] = 1 }, record.Dice.Damage);
        Assert.Contains(_slack.Posted, m => m.Text.Contains("g1: miss"));
        Assert.Contains("f3 white knight 2/3", _slack.Posted[^1].Blocks!.ToJsonString());
    }

    [Fact]
    public async Task A_custom_rule_is_run_by_the_referee_with_memory_questions_and_replies()
    {
        var thread = await StartAfterE4E5();
        var custom = new DiceRuleSpec(RuleTriggers.EveryTurn, 1, 0, [], "Rock-paper-scissors duel",
            Custom: "After every turn the player who just moved plays rock-paper-scissors against the referee...",
            InitialMemory: "{\"wins\":0}", CustomValue: 2);
        _judge.Ruling = FakeDiceJudge.AcceptOptions(
            [new DiceOption([], "Rock-paper-scissors rule", [custom])],
            [new DiceOption([new DiceEffect("remove", "a2", "", "", "")], "Your a2 pawn is removed")]);

        await Mention(Alice, "rock paper scissors every turn, if I fail I lose a pawn", thread);

        // Priced on the referee's estimate: worth 2, risking 1, so fair odds are 1/3 (14+).
        Assert.Equal(14, (await Record(thread)).Dice.Pending!.Target);
        _roller.Next = 20;
        await Click(Alice, ActionIds.Roll, thread);
        Assert.Empty(_judge.RuleCalls);

        // Bob moves: the rule fires, removes a white pawn, and asks Bob something.
        _judge.RuleResults.Enqueue(new CustomRuleResult(
            [new DiceEffect("remove", "a2", "", "", "")], "{\"wins\":1}", "The referee threw scissors.", "Referee wins: 1", "black", "Rock, paper or scissors?", false));
        await Say(Bob, "Nc6", thread);
        var call = _judge.RuleCalls.Single();
        Assert.Equal("{\"wins\":0}", call.Memory);
        Assert.Equal(PieceColor.White, call.Owner);
        Assert.Contains("black just finished their turn (last move: Nc6)", call.Event);
        Assert.Equal(12, call.RandomNumbers.Count);

        var record = await Record(thread);
        Assert.True(record.LoadGame().Current[Square.Parse("a2")].IsEmpty);
        Assert.Equal("{\"wins\":1}", record.Dice.Rules.Single().Memory);
        Assert.Contains(_slack.Posted, m => m.Text.Contains("The referee threw scissors.") && m.Text.Contains($"<@{Bob}>, Rock, paper or scissors?"));
        Assert.Contains("Referee wins: 1", _slack.Posted[^1].Blocks!.ToJsonString());

        // Alice can move, but Bob owes a reply first; a full sentence is fine for a custom question.
        await Say(Alice, "d4", thread);
        await Say(Bob, "Nf6", thread);
        Assert.Contains("Reply to this first", _slack.Ephemeral[^1].Text);
        _judge.RuleResults.Enqueue(new CustomRuleResult([], "{\"wins\":1}", "Rock beats scissors!", "", "", "", Expire: true));
        await Say(Bob, "I pick rock obviously", thread);
        Assert.Contains("<reply>I pick rock obviously</reply>", _judge.RuleCalls[^1].Event);

        record = await Record(thread);
        Assert.Empty(record.Dice.Rules);
        Assert.Empty(record.Dice.Riddles);
        Assert.Contains(_slack.Posted, m => m.Text.Contains("Rock beats scissors!") && m.Text.Contains("has ended"));
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
    [Fact]
    public void Damage_sinks_a_piece_once_its_hit_points_run_out()
    {
        var position = Position.FromFen("4k3/8/8/8/8/8/1P6/1N2K3 w - - 0 1");
        var context = new EffectContext();
        var shot = new[] { new DiceEffect(DiceRules.Damage, "b1", "", "", "") };
        for (var hit = 1; hit <= 2; hit++)
        {
            Assert.True(DiceRules.TryBuildEdits(position, shot, out var edits, out var error, null, context), error);
            Assert.Empty(edits);
            Assert.Equal(hit, context.Damage["b1"]);
        }

        Assert.True(DiceRules.TryBuildEdits(position, shot, out var sunk, out _, null, context));
        Assert.Equal(["b1=-"], sunk.Select(e => e.ToString()));
        Assert.Empty(context.Damage);

        // A pawn has one hit point; kings and empty squares are misses.
        Assert.True(DiceRules.TryBuildEdits(position, [new DiceEffect(DiceRules.Damage, "b2", "", "", "")], out var pawn, out _));
        Assert.Equal(["b2=-"], pawn.Select(e => e.ToString()));
        var misses = new EffectContext();
        Assert.True(DiceRules.TryBuildEdits(position,
            [new DiceEffect(DiceRules.Damage, "e1", "", "", ""), new DiceEffect(DiceRules.Damage, "d4", "", "", "")],
            out var none, out _, null, misses));
        Assert.Empty(none);
        Assert.Equal(["e1: bounces off the white king", "d4: miss"], misses.Log);
    }

    [Fact]
    public void Damage_follows_a_moving_piece_and_is_lost_when_it_is_captured()
    {
        var before = Position.FromFen("4k3/8/8/3p4/8/2N5/8/4K2R w - - 0 1");
        var after = before.WithEdits([SquareEdit.Parse("c3=-"), SquareEdit.Parse("d5=N")]);
        var damage = DiceRules.FollowDamage(before, after, new Dictionary<string, int> { ["c3"] = 2, ["d5"] = 0, ["h1"] = 4 });
        Assert.Equal(new Dictionary<string, int> { ["d5"] = 2, ["h1"] = 4 }, damage);
    }

    [Fact]
    public void Target_colours_point_at_the_player_a_rule_acts_on()
    {
        var effects = DiceRules.ForTarget(
            [new DiceEffect("add", DiceRules.RandomEmpty, "", "pawn", DiceRules.Target), new DiceEffect("remove", DiceRules.RandomTargetOpponent, "", "", "")],
            PieceColor.Black);
        Assert.Equal("black", effects[0].Color);
        Assert.Equal(DiceRules.RandomWhite, effects[1].Square);
    }

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
    public void Random_selectors_pick_from_the_right_squares()
    {
        // The only white non-king piece is the a2 pawn; the only empty squares a pawn may use are on ranks 2-7.
        var position = Position.FromFen("4k3/8/8/8/8/8/P7/4K3 w - - 0 1");
        Assert.True(DiceRules.TryBuildEdits(position, [new DiceEffect("remove", DiceRules.RandomWhite, "", "", "")],
            out var edits, out var error, _ => 0), error);
        Assert.Equal(["a2=-"], edits.Select(e => e.ToString()));

        Assert.True(DiceRules.TryBuildEdits(position, [new DiceEffect("add", DiceRules.RandomEmpty, "", "pawn", "black")],
            out edits, out error, _ => 0), error);
        Assert.Equal(["b2=p"], edits.Select(e => e.ToString()));

        Assert.False(DiceRules.TryBuildEdits(position, [new DiceEffect("remove", DiceRules.RandomBlack, "", "", "")],
            out _, out error, _ => 0));
        Assert.Contains("no square to pick", error);
    }

    [Fact]
    public void Jimmy_gambit_sacrifices_everything_but_the_king_and_its_shields()
    {
        // The e2 knight blocks the e8 rook and the d2 pawn blocks the a5 bishop (the c3 pawn behind it doesn't
        // count). The a1 rook, d1 queen, c3 pawn and f2 pawn shield nothing.
        var position = Position.FromFen("4r2k/8/8/b7/8/2P5/3PNP2/R2QK3 w - - 0 1");
        Assert.True(DiceRules.TryBuildEdits(position, [new DiceEffect(DiceRules.JimmyGambit, "", "", "", "white")],
            out var edits, out var error), error);

        Assert.Equal(["a1=-", "c3=-", "d1=-", "f2=-"], edits.Select(e => e.ToString()).Order());
        Assert.Equal(-16, DiceRules.MaterialDelta(position, edits, PieceColor.White));
        Assert.True(Game.ValidateEdits(position, edits, out error), error);
    }

    [Fact]
    public void Jimmy_gambit_needs_a_side()
    {
        Assert.False(DiceRules.TryBuildEdits(Position.Start, [new DiceEffect(DiceRules.JimmyGambit, "", "", "", "")], out _, out var error));
        Assert.Contains("Jimmy gambit", error);
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

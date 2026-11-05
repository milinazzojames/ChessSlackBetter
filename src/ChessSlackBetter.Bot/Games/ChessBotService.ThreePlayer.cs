using System.Text;
using ChessSlackBetter.Bot.Slack;
using ChessSlackBetter.Chess;
using ChessSlackBetter.Chess.ThreePlayer;

namespace ChessSlackBetter.Bot.Games;

/// <summary>Three-player chess: three players in one thread, last one standing wins.</summary>
public sealed partial class ChessBotService
{
    private async Task ChallengeThreePlayerAsync(IncomingMessage message, ThreePlayerChallengeCommand challenge, string botId, CancellationToken ct)
    {
        string[] invited = [challenge.FirstOpponentId, challenge.SecondOpponentId];
        if (invited.Contains(message.UserId))
        {
            await EphemeralAsync(message, "Mention the two *other* people you want to play — you're already in.", ct);
            return;
        }

        if (invited.Contains(botId))
        {
            await EphemeralAsync(message, "I'm just the referee. Challenge two humans!", ct);
            return;
        }

        var seats = new[] { message.UserId, challenge.FirstOpponentId, challenge.SecondOpponentId };
        Random.Shared.Shuffle(seats);
        var now = time.GetUtcNow();
        ThreePlayerRecord Create(string threadTs) => new()
        {
            ChannelId = message.ChannelId,
            ThreadTs = threadTs,
            ChallengerId = message.UserId,
            WhiteId = seats[0],
            BlackId = seats[1],
            RedId = seats[2],
            WithDice = challenge.WithDice,
            Accepted = [message.UserId],
            CreatedAt = now,
            UpdatedAt = now,
        };

        // The challenge is always a new top-level message so the game gets a thread of its own.
        var card = Messages.ThreePlayerChallengeCard(Create(""));
        var ts = await slack.PostMessageAsync(new SlackMessage(message.ChannelId, card.Text, card.Blocks), ct);
        await threePlayerGames.SaveAsync(Create(ts), ct);
    }

    private async Task HandleThreePlayerCommandAsync(ThreePlayerRecord record, IncomingMessage message, BotCommand command, CancellationToken ct)
    {
        var reply = (string text) => slack.PostEphemeralAsync(message.ChannelId, message.UserId, record.ThreadTs, text, ct);
        switch (command)
        {
            case AcceptCommand:
                await AcceptThreePlayerAsync(record, message.UserId, reply, ct);
                break;
            case DeclineCommand when record.Dice.Pending?.RequesterId == message.UserId:
            case CancelRollCommand:
                await CancelThreePlayerRollAsync(record, message.UserId, reply, ct);
                break;
            case DeclineCommand:
                await DeclineThreePlayerAsync(record, message.UserId, reply, ct);
                break;
            case MoveCommand move:
                await MoveThreePlayerAsync(record, message.UserId, move.Move, reply, ct);
                break;
            case UnknownCommand unknown when TriNotation.LooksLikeMove(unknown.Text):
                await MoveThreePlayerAsync(record, message.UserId, unknown.Text, reply, ct);
                break;
            case ResignCommand:
                await ResignThreePlayerAsync(record, message.UserId, reply, ct);
                break;
            case DrawCommand:
                await OfferThreePlayerDrawAsync(record, message.UserId, reply, ct);
                break;
            case DeclineDrawCommand:
                await DeclineThreePlayerDrawAsync(record, message.UserId, reply, ct);
                break;
            case AbortCommand:
                await AbortThreePlayerAsync(record, message.UserId, reply, ct);
                break;
            case BoardCommand:
                await ShowThreePlayerBoardAsync(record, reply, ct);
                break;
            case PgnCommand:
                await ShowThreePlayerMovesAsync(record, reply, ct);
                break;
            case RollRequestCommand roll:
                await RequestThreePlayerRollAsync(record, message.UserId, roll.Request, reply, ct);
                break;
            case UnknownCommand unknown when record.WithDice && record.State == GameState.Active:
                await RequestThreePlayerRollAsync(record, message.UserId, unknown.Text, reply, ct);
                break;
            case ConfirmRollCommand:
                await ConfirmThreePlayerRollAsync(record, message.UserId, reply, ct);
                break;
            case VetoCommand:
                await VetoThreePlayerAsync(record, message.UserId, reply, ct);
                break;
            case UndoCommand:
                await reply("Undo is only allowed in normal chess games.");
                break;
            case UnknownCommand unknown:
                await reply(UnknownText(unknown));
                break;
        }
    }

    private async Task HandleThreePlayerActionAsync(ThreePlayerRecord record, IncomingAction action, CancellationToken ct)
    {
        var reply = (string text) => slack.PostEphemeralAsync(action.ChannelId, action.UserId, record.ThreadTs, text, ct);
        switch (action.ActionId)
        {
            case ActionIds.Accept:
                await AcceptThreePlayerAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Decline:
                await DeclineThreePlayerAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Resign:
                await ResignThreePlayerAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.OfferDraw or ActionIds.AcceptDraw:
                await OfferThreePlayerDrawAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.DeclineDraw:
                await DeclineThreePlayerDrawAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Roll:
                await ConfirmThreePlayerRollAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.CancelRoll:
                await CancelThreePlayerRollAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Veto:
                await VetoThreePlayerAsync(record, action.UserId, reply, ct);
                break;
        }
    }

    private async Task AcceptThreePlayerAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.State != GameState.Challenged)
        {
            await reply("This challenge has already been answered.");
            return;
        }

        if (!record.IsPlayer(userId))
        {
            await reply("This challenge isn't for you — but feel free to watch!");
            return;
        }

        if (!record.Accepted.Contains(userId))
        {
            record.Accepted.Add(userId);
        }

        var waiting = record.Players.Where(p => !record.Accepted.Contains(p)).ToList();
        if (waiting.Count > 0)
        {
            await SaveThreePlayerAsync(record, ct);
            var card = Messages.ThreePlayerChallengeCard(record);
            await slack.UpdateMessageAsync(record.ChannelId, record.ThreadTs, card.Text, card.Blocks, ct);
            return;
        }

        record.State = GameState.Active;
        await SaveThreePlayerAsync(record, ct);
        var game = record.LoadGame();
        await UpdateThreePlayerCardAsync(record, game, ct);
        await PostThreePlayerBoardAsync(record, game, ct);
    }

    private async Task DeclineThreePlayerAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.State != GameState.Challenged)
        {
            await reply(record.State == GameState.Active
                ? "The game has already started. Use `@ChessBetterBot resign` or `@ChessBetterBot abort` instead."
                : "This challenge has already been answered.");
            return;
        }

        if (!record.IsPlayer(userId))
        {
            await reply("This isn't your challenge.");
            return;
        }

        record.State = GameState.Declined;
        record.DeclinedBy = userId;
        await SaveThreePlayerAsync(record, ct);
        await UpdateThreePlayerCardAsync(record, null, ct);
    }

    private async Task MoveThreePlayerAsync(ThreePlayerRecord record, string userId, string input, Func<string, Task> reply, CancellationToken ct)
    {
        if (await ActiveThreePlayerGameAsync(record, userId, reply) is not { } game)
        {
            return;
        }

        var color = record.ColorOf(userId);
        if (game.Current.SideToMove != color)
        {
            await reply($"It's not your turn — waiting on <@{record.PlayerFor(game.Current.SideToMove)}>.");
            return;
        }

        var eliminated = game.Eliminations.Count;
        if (!game.TryPlay(input, out _, out var error))
        {
            await reply(error);
            return;
        }

        record.DrawAgreed.Clear();

        // Moving instead of rolling withdraws a waiting gamble.
        if (record.Dice.Pending is { } pending)
        {
            record.Dice.Pending = null;
            await CloseThreePlayerProposalAsync(record, pending, "_Withdrawn: they moved instead._", ct);
        }

        await AfterThreePlayerTurnAsync(record, game, eliminated, ct);
    }

    private async Task ResignThreePlayerAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (await ActiveThreePlayerGameAsync(record, userId, reply) is not { } game)
        {
            return;
        }

        var eliminated = game.Eliminations.Count;
        var wasTheirTurn = game.Current.SideToMove == record.ColorOf(userId);
        game.Resign(record.ColorOf(userId));
        if (record.Dice.Pending is { } pending && pending.RequesterId == userId)
        {
            record.Dice.Pending = null;
            await CloseThreePlayerProposalAsync(record, pending, "_Withdrawn: they resigned._", ct);
        }
        await AfterThreePlayerTurnAsync(record, game, eliminated, ct, postBoard: wasTheirTurn);
    }

    /// <summary>Saves, announces anyone knocked out, then shows the board or the result.</summary>
    private async Task AfterThreePlayerTurnAsync(ThreePlayerRecord record, TriGame game, int eliminatedBefore, CancellationToken ct, bool postBoard = true)
    {
        record.SyncFrom(game);
        await SaveThreePlayerAsync(record, ct);

        foreach (var elimination in game.Eliminations.Skip(eliminatedBefore))
        {
            await slack.PostMessageAsync(new SlackMessage(record.ChannelId, Messages.ThreePlayerElimination(record, elimination), ThreadTs: record.ThreadTs), ct);
        }

        if (game.IsOver)
        {
            await FinishThreePlayerAsync(record, game, ct);
        }
        else if (postBoard || game.Eliminations.Count > eliminatedBefore)
        {
            await PostThreePlayerBoardAsync(record, game, ct);
        }

        if (game.Moves.Count > 0)
        {
            var last = game.Moves[^1];
            var situation = $"{(record.WithDice ? Messages.ThreePlayerDiceName : Messages.ThreePlayerName)}, on a triangular board with files a-l and ranks 1-12. " +
                            (last.IsDiceRoll ? $"{last.Player.Name()} just won or lost a dice gamble. " : $"{last.Player.Name()} just played {last.San}. ") +
                            string.Concat(game.Eliminations.Select(e => $"{e.Player.Name()} has been knocked out. ")) +
                            (game.IsOver ? "The game is over." : "");
            await PostQuoteAsync(record.ChannelId, record.ThreadTs, situation, ct);
        }
    }

    private async Task OfferThreePlayerDrawAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (await ActiveThreePlayerGameAsync(record, userId, reply) is not { } game)
        {
            return;
        }

        if (record.DrawAgreed.Contains(userId))
        {
            await reply("You've already agreed to a draw. Waiting on the others.");
            return;
        }

        var offeredBy = record.DrawAgreed.FirstOrDefault() ?? userId;
        record.DrawAgreed.Add(userId);
        var stillToAgree = game.Current.AliveColors.Select(record.PlayerFor).Where(p => !record.DrawAgreed.Contains(p)).ToList();
        if (stillToAgree.Count == 0)
        {
            game.AgreeDraw();
            record.SyncFrom(game);
            await SaveThreePlayerAsync(record, ct);
            await FinishThreePlayerAsync(record, game, ct);
            return;
        }

        await SaveThreePlayerAsync(record, ct);
        var offer = Messages.ThreePlayerDrawOffer(record, offeredBy, stillToAgree);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, offer.Text, offer.Blocks, record.ThreadTs), ct);
    }

    private async Task DeclineThreePlayerDrawAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (await ActiveThreePlayerGameAsync(record, userId, reply) is null)
        {
            return;
        }

        if (record.DrawAgreed.Count == 0 || record.DrawAgreed.Contains(userId))
        {
            await reply("There's no draw offer for you to decline.");
            return;
        }

        record.DrawAgreed.Clear();
        await SaveThreePlayerAsync(record, ct);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, $"<@{userId}> declined the draw. Play on!", ThreadTs: record.ThreadTs), ct);
    }

    private async Task AbortThreePlayerAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (await ActiveThreePlayerGameAsync(record, userId, reply) is not { } game)
        {
            return;
        }

        if (game.Moves.Count >= 3 || game.Eliminations.Count > 0)
        {
            await reply("Everyone has moved, so the game can't be aborted. You can `resign` or offer a `draw`.");
            return;
        }

        game.Abandon();
        record.SyncFrom(game);
        await SaveThreePlayerAsync(record, ct);
        await FinishThreePlayerAsync(record, game, ct);
    }

    private async Task ShowThreePlayerBoardAsync(ThreePlayerRecord record, Func<string, Task> reply, CancellationToken ct)
    {
        var game = record.LoadGame();
        switch (record.State)
        {
            case GameState.Active:
                await PostThreePlayerBoardAsync(record, game, ct);
                break;
            case GameState.Finished:
                var over = Messages.ThreePlayerGameOver(record, game, Options.PublicBaseUrl);
                await slack.PostMessageAsync(new SlackMessage(record.ChannelId, over.Text, over.Blocks, record.ThreadTs), ct);
                break;
            default:
                await reply("The game hasn't started yet.");
                break;
        }
    }

    private async Task ShowThreePlayerMovesAsync(ThreePlayerRecord record, Func<string, Task> reply, CancellationToken ct)
    {
        var game = record.LoadGame();
        if (game.Moves.Count == 0)
        {
            await reply("No moves have been played yet.");
            return;
        }

        var content = Messages.ThreePlayerMoves(game);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, content.Text, content.Blocks, record.ThreadTs), ct);
    }

    /// <summary>The game, if it's running and <paramref name="userId"/> is still in it; otherwise explains why not.</summary>
    private async Task<TriGame?> ActiveThreePlayerGameAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply)
    {
        var waiting = record.Players.Where(p => !record.Accepted.Contains(p)).Select(p => $"<@{p}>");
        var problem = record.State switch
        {
            GameState.Challenged => $"The game hasn't started — {string.Join(" and ", waiting)} still need to accept.",
            GameState.Declined => "That challenge was declined.",
            GameState.Finished => "This game is over. Start a new one with `@ChessBetterBot threeplayerchess @someone @someone`.",
            _ when !record.IsPlayer(userId) => "You're not playing in this game — but feel free to watch!",
            _ => null,
        };

        var game = problem is null ? record.LoadGame() : null;
        if (game is not null && game.IsEliminated(record.ColorOf(userId)))
        {
            problem = "You're out of this game — but feel free to keep watching!";
        }

        if (problem is not null)
        {
            await reply(problem);
            return null;
        }

        return game;
    }

    private async Task PostThreePlayerBoardAsync(ThreePlayerRecord record, TriGame game, CancellationToken ct)
    {
        var board = Messages.ThreePlayerBoard(record, game, Options.PublicBaseUrl);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, board.Text, board.Blocks, record.ThreadTs), ct);
    }

    private async Task FinishThreePlayerAsync(ThreePlayerRecord record, TriGame game, CancellationToken ct)
    {
        var over = Messages.ThreePlayerGameOver(record, game, Options.PublicBaseUrl);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, over.Text, over.Blocks, record.ThreadTs, Broadcast: true), ct);
        await UpdateThreePlayerCardAsync(record, game, ct);
    }

    private async Task UpdateThreePlayerCardAsync(ThreePlayerRecord record, TriGame? game, CancellationToken ct)
    {
        var card = Messages.ThreePlayerStatusCard(record, game);
        await slack.UpdateMessageAsync(record.ChannelId, record.ThreadTs, card.Text, card.Blocks, ct);
    }

    private Task SaveThreePlayerAsync(ThreePlayerRecord record, CancellationToken ct)
    {
        record.UpdatedAt = time.GetUtcNow();
        return threePlayerGames.SaveAsync(record, ct);
    }

    private async Task<List<string>> ThreePlayerOpenGameLinesAsync(string userId, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var record in await threePlayerGames.GetOpenGamesAsync(userId, ct))
        {
            var link = await slack.GetPermalinkAsync(record.ChannelId, record.ThreadTs, ct);
            var others = string.Join(" and ", record.Players.Where(p => p != userId).Select(p => $"<@{p}>"));
            var status = record.State == GameState.Challenged
                ? (record.Accepted.Contains(userId) ? "waiting for the others to accept" : "waiting for you to accept")
                : record.LoadGame() is var game && game.IsEliminated(record.ColorOf(userId)) ? "you're out"
                : game.Current.SideToMove == record.ColorOf(userId) ? "*your move*" : "their move";
            var kind = record.WithDice ? "3-player dice" : "3-player";
            var label = link is null ? $"{kind} vs {others}" : $"<{link}|{kind} vs {others}>";
            lines.Add($"• {label} — {status}");
        }

        return lines;
    }

    /// <summary>
    /// Points per finished game: 1 for first, ½ for second, 0 for third. Players who share a place split the points
    /// for the places they cover, so a two-way draw for first is ¾ each.
    /// </summary>
    private async Task<List<ThreePlayerStanding>> ThreePlayerStandingsAsync(bool withDice, CancellationToken ct)
    {
        double[] placePoints = [1, 0.5, 0];
        var table = new Dictionary<string, ThreePlayerStanding>();
        ThreePlayerStanding For(string user) => table.TryGetValue(user, out var s) ? s : table[user] = new ThreePlayerStanding(user);
        foreach (var record in await threePlayerGames.GetFinishedGamesAsync(ct))
        {
            var game = record.LoadGame();
            if (game.Termination == Termination.Abandoned || record.WithDice != withDice)
            {
                continue;
            }

            foreach (var roll in record.Dice.Log)
            {
                var roller = For(roll.UserId);
                roller.RollsTaken++;
                roller.RollsWon += roll.Success ? 1 : 0;
            }

            var place = 0;
            foreach (var group in game.Ranking)
            {
                var points = placePoints.Skip(place).Take(group.Count).Average();
                foreach (var color in group)
                {
                    var standing = For(record.PlayerFor(color));
                    standing.Places[place]++;
                    standing.Points += points;
                }

                place += group.Count;
            }
        }

        return table.Values.OrderByDescending(s => s.Points).ThenByDescending(s => s.Games).ToList();
    }

    private async Task ThreePlayerLeaderboardAsync(IncomingMessage message, bool withDice, CancellationToken ct)
    {
        var standings = (await ThreePlayerStandingsAsync(withDice, ct)).Where(s => s.Games > 0).ToList();
        var name = withDice ? Messages.ThreePlayerDiceName : Messages.ThreePlayerName;
        if (standings.Count == 0)
        {
            await slack.PostMessageAsync(new SlackMessage(message.ChannelId, $"No {name} games have been finished yet.", ThreadTs: message.ThreadTs), ct);
            return;
        }

        var sb = new StringBuilder($":small_red_triangle: *{name} Leaderboard* (1 point for 1st, ½ for 2nd)\n");
        var rank = 1;
        foreach (var s in standings.Take(10))
        {
            var gambles = withDice ? $" · gambles won {s.RollsWon}/{s.RollsTaken}" : "";
            sb.AppendLine($"{rank++}. <@{s.UserId}> — *{s.Points:0.##}* pts ({s.Line}){gambles}");
        }

        await slack.PostMessageAsync(new SlackMessage(message.ChannelId, sb.ToString(), ThreadTs: message.ThreadTs), ct);
    }

    private sealed class ThreePlayerStanding(string userId)
    {
        public string UserId { get; } = userId;

        /// <summary>How often the player finished 1st, 2nd and 3rd (shared places count for each player).</summary>
        public int[] Places { get; } = new int[3];

        public double Points { get; set; }

        public int RollsTaken { get; set; }

        public int RollsWon { get; set; }

        public int Games => Places.Sum();

        public string Line => $"{Places[0]}× 1st, {Places[1]}× 2nd, {Places[2]}× 3rd";
    }
}

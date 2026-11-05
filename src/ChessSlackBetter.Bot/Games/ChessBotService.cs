using System.Text;
using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Slack;
using ChessSlackBetter.Bot.Users;
using ChessSlackBetter.Chess;
using ChessSlackBetter.Chess.ThreePlayer;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Games;

/// <summary>A message that mentions the bot, or (when <see cref="IsMention"/> is false) any message in a thread.</summary>
public sealed record IncomingMessage(string ChannelId, string UserId, string Text, string Ts, string? ThreadTs, bool IsMention);

/// <summary>A button click. <see cref="ThreadTs"/> is the game thread the clicked message belongs to.</summary>
public sealed record IncomingAction(string ActionId, string UserId, string ChannelId, string ThreadTs);

public sealed partial class ChessBotService(
    ISlackApi slack,
    IGameRepository games,
    IUserRepository users,
    IThreePlayerRepository threePlayerGames,
    IAnalysisLinks analysis,
    IDiceJudge judge,
    IDiceRoller dice,
    IOptions<ChessBotOptions> options,
    TimeProvider time,
    ILogger<ChessBotService> logger)
{
    private ChessBotOptions Options => options.Value;

    public async Task HandleMessageAsync(IncomingMessage message, CancellationToken ct = default)
    {
        var botId = await slack.GetBotUserIdAsync(ct);
        if (message.UserId == botId)
        {
            return;
        }

        if (!message.IsMention)
        {
            await HandleUnaddressedThreadMessageAsync(message, botId, ct);
            return;
        }

        var command = CommandParser.Parse(CommandParser.StripMention(message.Text, botId));
        logger.LogInformation("{User} in {Channel}: {Command}", message.UserId, message.ChannelId, command);

        switch (command)
        {
            case HelpCommand:
                await EphemeralAsync(message, Messages.HelpText, ct);
                return;
            case UnknownCommand unknown when message.ThreadTs is null:
                await EphemeralAsync(message, UnknownText(unknown), ct);
                return;
            case ChallengeCommand challenge:
                await ChallengeAsync(message, challenge, botId, ct);
                return;
            case ThreePlayerChallengeCommand challenge:
                await ChallengeThreePlayerAsync(message, challenge, botId, ct);
                return;
            case GamesCommand:
                await ListGamesAsync(message, ct);
                return;
            case StatsCommand stats:
                await StatsAsync(message, stats.UserId ?? message.UserId, ct);
                return;
            case LeaderboardCommand leaderboard:
                await LeaderboardAsync(message, leaderboard.Mode, ct);
                return;
            case ThemeCommand theme:
                await ThemeAsync(message, theme.Name, ct);
                return;
            case PiecesCommand pieces:
                await PiecesAsync(message, pieces.Name, ct);
                return;
        }

        // Everything else is about the game in this thread.
        var record = message.ThreadTs is null ? null : await games.GetAsync(message.ChannelId, message.ThreadTs, ct);
        if (record is null)
        {
            if (message.ThreadTs is not null && await threePlayerGames.GetAsync(message.ChannelId, message.ThreadTs, ct) is { } threePlayer)
            {
                await HandleThreePlayerCommandAsync(threePlayer, message, command, ct);
                return;
            }

            await EphemeralAsync(message, "There's no game in this thread. Start one with `@ChessBetterBot challenge @someone`.", ct);
            return;
        }

        var reply = (string text) => slack.PostEphemeralAsync(message.ChannelId, message.UserId, record.ThreadTs, text, ct);
        switch (command)
        {
            case AcceptCommand:
                await AcceptAsync(record, message.UserId, reply, ct);
                break;
            case DeclineCommand when record.Dice.Pending?.RequesterId == message.UserId:
            case CancelRollCommand:
                await CancelRollAsync(record, message.UserId, reply, ct);
                break;
            case DeclineCommand:
                await DeclineAsync(record, message.UserId, reply, ct);
                break;
            case RollRequestCommand roll:
                await RequestRollAsync(record, message.UserId, roll.Request, reply, ct);
                break;
            case UnknownCommand unknown when record.Mode == GameMode.DiceRoll && record.State == GameState.Active:
                await RequestRollAsync(record, message.UserId, unknown.Text, reply, ct);
                break;
            case UnknownCommand unknown:
                await reply(UnknownText(unknown));
                break;
            case ConfirmRollCommand:
                await ConfirmRollAsync(record, message.UserId, reply, ct);
                break;
            case VetoCommand:
                await VetoAsync(record, message.UserId, reply, ct);
                break;
            case MoveCommand move:
                await MoveAsync(record, message.UserId, move.Move, reply, ct);
                break;
            case ResignCommand:
                await ResignAsync(record, message.UserId, reply, ct);
                break;
            case DrawCommand:
                await OfferDrawAsync(record, message.UserId, reply, ct);
                break;
            case DeclineDrawCommand:
                await DeclineDrawAsync(record, message.UserId, reply, ct);
                break;
            case AbortCommand:
                await AbortAsync(record, message.UserId, reply, ct);
                break;
            case BoardCommand:
                await ShowBoardAsync(record, reply, ct);
                break;
            case PgnCommand:
                await ShowPgnAsync(record, reply, ct);
                break;
        }
    }

    public async Task HandleActionAsync(IncomingAction action, CancellationToken ct = default)
    {
        var record = await games.GetAsync(action.ChannelId, action.ThreadTs, ct);
        if (record is null && await threePlayerGames.GetAsync(action.ChannelId, action.ThreadTs, ct) is { } threePlayer)
        {
            await HandleThreePlayerActionAsync(threePlayer, action, ct);
            return;
        }

        if (record is null)
        {
            logger.LogWarning("Button {Action} clicked for unknown game {Channel}/{Thread}", action.ActionId, action.ChannelId, action.ThreadTs);
            return;
        }

        var reply = (string text) => slack.PostEphemeralAsync(action.ChannelId, action.UserId, record.ThreadTs, text, ct);
        switch (action.ActionId)
        {
            case ActionIds.Accept:
                await AcceptAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Decline:
                await DeclineAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Resign:
                await ResignAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.OfferDraw or ActionIds.AcceptDraw:
                await OfferDrawAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.DeclineDraw:
                await DeclineDrawAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Roll:
                await ConfirmRollAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.CancelRoll:
                await CancelRollAsync(record, action.UserId, reply, ct);
                break;
            case ActionIds.Veto:
                await VetoAsync(record, action.UserId, reply, ct);
                break;
        }
    }

    private static string UnknownText(UnknownCommand unknown) =>
        $"Sorry, I don't understand `{unknown.Text}`. Try `@ChessBetterBot help`.";

    /// <summary>Lets players type "e4" in the game thread without mentioning the bot.</summary>
    private async Task HandleUnaddressedThreadMessageAsync(IncomingMessage message, string botId, CancellationToken ct)
    {
        // Mentions arrive separately as app_mention events, so skip them here to avoid handling them twice.
        if (!Options.AllowMovesWithoutMention || message.ThreadTs is null || message.Text.Contains($"<@{botId}"))
        {
            return;
        }

        var text = message.Text.Trim();
        var command = CommandParser.Parse(text);
        var isDiceAnswer = command is ConfirmRollCommand or CancelRollCommand or VetoCommand;
        var looksLikeThreePlayerMove = TriNotation.LooksLikeMove(text);
        if (!Notation.LooksLikeMove(text) && !isDiceAnswer && !looksLikeThreePlayerMove)
        {
            return;
        }

        Func<string, Task> reply = t => slack.PostEphemeralAsync(message.ChannelId, message.UserId, message.ThreadTs, t, ct);
        var record = await games.GetAsync(message.ChannelId, message.ThreadTs, ct);
        if (record is null)
        {
            if (await threePlayerGames.GetAsync(message.ChannelId, message.ThreadTs, ct) is not { State: GameState.Active } threePlayer ||
                !threePlayer.IsPlayer(message.UserId))
            {
                return;
            }

            if (!isDiceAnswer)
            {
                if (looksLikeThreePlayerMove)
                {
                    await MoveThreePlayerAsync(threePlayer, message.UserId, text, reply, ct);
                }

                return;
            }

            // As in two-player games, "yes" / "no" / "veto" only count from the right player while a gamble is waiting.
            var requester = threePlayer.Dice.Pending?.RequesterId == message.UserId;
            if (threePlayer.Dice.Pending is null || (command is VetoCommand ? requester : !requester))
            {
                return;
            }

            await (command switch
            {
                ConfirmRollCommand => ConfirmThreePlayerRollAsync(threePlayer, message.UserId, reply, ct),
                CancelRollCommand => CancelThreePlayerRollAsync(threePlayer, message.UserId, reply, ct),
                _ => VetoThreePlayerAsync(threePlayer, message.UserId, reply, ct),
            });
            return;
        }

        if (record.State != GameState.Active || !record.IsPlayer(message.UserId) || (!Notation.LooksLikeMove(text) && !isDiceAnswer))
        {
            return;
        }
        if (!isDiceAnswer)
        {
            await MoveAsync(record, message.UserId, text, reply, ct);
            return;
        }

        // "yes" / "no" / "veto" only mean something from the right player while a gamble is waiting;
        // otherwise it's just chat.
        var isRequester = record.Dice.Pending?.RequesterId == message.UserId;
        if (record.Dice.Pending is null || (command is VetoCommand ? isRequester : !isRequester))
        {
            return;
        }

        switch (command)
        {
            case ConfirmRollCommand:
                await ConfirmRollAsync(record, message.UserId, reply, ct);
                break;
            case CancelRollCommand:
                await CancelRollAsync(record, message.UserId, reply, ct);
                break;
            case VetoCommand:
                await VetoAsync(record, message.UserId, reply, ct);
                break;
        }
    }

    private async Task ChallengeAsync(IncomingMessage message, ChallengeCommand challenge, string botId, CancellationToken ct)
    {
        if (challenge.OpponentId == message.UserId)
        {
            await EphemeralAsync(message, "You can't challenge yourself — mention someone else.", ct);
            return;
        }

        if (challenge.OpponentId == botId)
        {
            await EphemeralAsync(message, "I'm just the referee. Challenge a human!", ct);
            return;
        }

        var challengerIsWhite = challenge.ChallengerColor switch
        {
            PieceColor.White => true,
            PieceColor.Black => false,
            _ => Random.Shared.Next(2) == 0,
        };
        var whiteId = challengerIsWhite ? message.UserId : challenge.OpponentId;
        var blackId = challengerIsWhite ? challenge.OpponentId : message.UserId;

        // Any shuffle but 518, which is just the normal starting position.
        int? chess960Number = null;
        if (challenge.Mode == GameMode.Chess960)
        {
            var n = Random.Shared.Next(Chess960.Count - 1);
            chess960Number = n >= Chess960.StandardNumber ? n + 1 : n;
        }

        // The challenge is always a new top-level message so each game gets a thread of its own.
        var card = Messages.ChallengeCard(message.UserId, challenge.OpponentId, whiteId, challenge.Mode, chess960Number);
        var ts = await slack.PostMessageAsync(new SlackMessage(message.ChannelId, card.Text, card.Blocks), ct);

        var now = time.GetUtcNow();
        await games.SaveAsync(new GameRecord
        {
            ChannelId = message.ChannelId,
            ThreadTs = ts,
            Mode = challenge.Mode,
            Chess960Number = chess960Number,
            ChallengerId = message.UserId,
            OpponentId = challenge.OpponentId,
            WhiteId = whiteId,
            BlackId = blackId,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);
    }

    private async Task AcceptAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.State != GameState.Challenged)
        {
            await reply("This challenge has already been answered.");
            return;
        }

        if (userId != record.OpponentId)
        {
            await reply(userId == record.ChallengerId
                ? $"Waiting for <@{record.OpponentId}> to accept."
                : $"Only <@{record.OpponentId}> can accept this challenge.");
            return;
        }

        record.State = GameState.Active;
        await SaveAsync(record, ct);

        var game = record.LoadGame();
        await UpdateCardAsync(record, game, ct);
        await PostBoardAsync(record, game, ct);
    }

    private async Task DeclineAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.State != GameState.Challenged)
        {
            await reply(record.State == GameState.Active
                ? "The game has already started. Use `@ChessBetterBot resign` or `@ChessBetterBot abort` instead."
                : "This challenge has already been answered.");
            return;
        }

        if (userId != record.OpponentId && userId != record.ChallengerId)
        {
            await reply("This isn't your challenge.");
            return;
        }

        record.State = GameState.Declined;

        // Abandoned marks a challenge withdrawn by the challenger rather than declined by the opponent.
        record.Termination = userId == record.ChallengerId ? Termination.Abandoned : Termination.None;
        await SaveAsync(record, ct);
        await UpdateCardAsync(record, null, ct);
    }

    private async Task MoveAsync(GameRecord record, string userId, string input, Func<string, Task> reply, CancellationToken ct)
    {
        if (!await EnsureActivePlayerAsync(record, userId, reply))
        {
            return;
        }

        var game = record.LoadGame();
        var color = record.ColorOf(userId);
        if (game.Current.SideToMove != color)
        {
            await reply($"It's not your turn — waiting on <@{record.PlayerFor(game.Current.SideToMove)}>.");
            return;
        }

        if (!game.TryPlay(input, out _, out var error))
        {
            await reply(error);
            return;
        }

        // Moving instead of rolling withdraws a waiting gamble.
        if (record.Dice.Pending is { } pending)
        {
            record.Dice.Pending = null;
            await CloseProposalAsync(record, pending, "_Withdrawn: they moved instead._", ct);
        }

        record.SyncFrom(game);
        record.DrawOfferedBy = null;
        await SaveAsync(record, ct);

        if (game.IsOver)
        {
            await FinishAsync(record, game, ct);
        }
        else
        {
            await PostBoardAsync(record, game, ct);
        }
    }

    private async Task ResignAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (!await EnsureActivePlayerAsync(record, userId, reply))
        {
            return;
        }

        var game = record.LoadGame();
        game.Resign(record.ColorOf(userId));
        record.SyncFrom(game);
        await SaveAsync(record, ct);
        await FinishAsync(record, game, ct);
    }

    private async Task OfferDrawAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (!await EnsureActivePlayerAsync(record, userId, reply))
        {
            return;
        }

        var opponent = record.OpponentOf(userId);
        if (record.DrawOfferedBy == opponent)
        {
            var game = record.LoadGame();
            game.AgreeDraw();
            record.SyncFrom(game);
            await SaveAsync(record, ct);
            await FinishAsync(record, game, ct);
            return;
        }

        if (record.DrawOfferedBy == userId)
        {
            await reply($"You've already offered a draw. Waiting on <@{opponent}>.");
            return;
        }

        record.DrawOfferedBy = userId;
        await SaveAsync(record, ct);
        var offer = Messages.DrawOffer(userId, opponent);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, offer.Text, offer.Blocks, record.ThreadTs), ct);
    }

    private async Task DeclineDrawAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (!await EnsureActivePlayerAsync(record, userId, reply))
        {
            return;
        }

        if (record.DrawOfferedBy != record.OpponentOf(userId))
        {
            await reply("There's no draw offer for you to decline.");
            return;
        }

        record.DrawOfferedBy = null;
        await SaveAsync(record, ct);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, $"<@{userId}> declined the draw. Play on!", ThreadTs: record.ThreadTs), ct);
    }

    private async Task AbortAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (!await EnsureActivePlayerAsync(record, userId, reply))
        {
            return;
        }

        if (record.Moves.Count >= 2)
        {
            await reply("Both players have moved, so the game can't be aborted. You can `resign` or offer a `draw`.");
            return;
        }

        var game = record.LoadGame();
        game.Abandon();
        record.SyncFrom(game);
        await SaveAsync(record, ct);
        await FinishAsync(record, game, ct);
    }

    private async Task ShowBoardAsync(GameRecord record, Func<string, Task> reply, CancellationToken ct)
    {
        var game = record.LoadGame();
        switch (record.State)
        {
            case GameState.Active:
                await PostBoardAsync(record, game, ct);
                break;
            case GameState.Finished:
                await PostGameOverAsync(record, game, broadcast: false, ct);
                break;
            default:
                await reply("The game hasn't started yet.");
                break;
        }
    }

    private async Task ShowPgnAsync(GameRecord record, Func<string, Task> reply, CancellationToken ct)
    {
        var game = record.LoadGame();
        if (game.Moves.Count == 0)
        {
            await reply("No moves have been played yet.");
            return;
        }

        var pgn = await BuildPgnAsync(record, game, ct);
        var content = Messages.Pgn(pgn, AnalysisUrl(record, game));
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, content.Text, content.Blocks, record.ThreadTs), ct);
    }

    private async Task ListGamesAsync(IncomingMessage message, CancellationToken ct)
    {
        var open = await games.GetOpenGamesAsync(message.UserId, ct);
        var threePlayer = await ThreePlayerOpenGameLinesAsync(message.UserId, ct);
        if (open.Count == 0 && threePlayer.Count == 0)
        {
            await EphemeralAsync(message, "You have no open games. Start one with `@ChessBetterBot challenge @someone`.", ct);
            return;
        }

        var sb = new StringBuilder("*Your open games*\n");
        foreach (var record in open)
        {
            var link = await slack.GetPermalinkAsync(record.ChannelId, record.ThreadTs, ct);
            var opponent = record.OpponentOf(message.UserId);
            var status = record.State == GameState.Challenged
                ? (record.OpponentId == message.UserId ? "waiting for you to accept" : "waiting for them to accept")
                : record.LoadGame().Current.SideToMove == record.ColorOf(message.UserId) ? "*your move*" : "their move";
            var label = link is null ? $"vs <@{opponent}>" : $"<{link}|vs <@{opponent}>>";
            sb.AppendLine($"• {label} — {status}");
        }

        foreach (var line in threePlayer)
        {
            sb.AppendLine(line);
        }

        await EphemeralAsync(message, sb.ToString(), ct);
    }

    private async Task StatsAsync(IncomingMessage message, string userId, CancellationToken ct)
    {
        var standard = (await StandingsAsync(GameMode.Standard, ct)).FirstOrDefault(s => s.UserId == userId);
        var diceGames = (await StandingsAsync(GameMode.DiceRoll, ct)).FirstOrDefault(s => s.UserId == userId);
        var chess960 = (await StandingsAsync(GameMode.Chess960, ct)).FirstOrDefault(s => s.UserId == userId);
        var threePlayer = (await ThreePlayerStandingsAsync(withDice: false, ct)).FirstOrDefault(s => s.UserId == userId && s.Games > 0);
        var threePlayerDice = (await ThreePlayerStandingsAsync(withDice: true, ct)).FirstOrDefault(s => s.UserId == userId && s.Games > 0);
        string Line(Standing s) => $"*{s.Wins}* wins, *{s.Losses}* losses, *{s.Draws}* draws ({s.Games} games) · rating *{s.Rating:0}*";

        var sb = new StringBuilder();
        if (standard is null && diceGames is null && chess960 is null && threePlayer is null && threePlayerDice is null)
        {
            sb.Append($"<@{userId}> hasn't finished any games yet.");
        }
        else
        {
            sb.AppendLine($"*Stats for <@{userId}>*");
            if (standard is not null)
            {
                sb.AppendLine($":chess_pawn: Chess: {Line(standard)}");
            }

            if (diceGames is not null)
            {
                sb.AppendLine($":game_die: ChessDiceRoll: {Line(diceGames)}");
                sb.AppendLine($":game_die: Gambles: *{diceGames.RollsWon}* of {diceGames.RollsTaken} won");
            }

            if (chess960 is not null)
            {
                sb.AppendLine($":twisted_rightwards_arrows: {Messages.Chess960Name}: {Line(chess960)}");
            }

            if (threePlayer is not null)
            {
                sb.AppendLine($":small_red_triangle: {Messages.ThreePlayerName}: {threePlayer.Line} ({threePlayer.Games} games)");
            }

            if (threePlayerDice is not null)
            {
                sb.AppendLine($":small_red_triangle::game_die: {Messages.ThreePlayerDiceName}: {threePlayerDice.Line} ({threePlayerDice.Games} games)" +
                              $" · gambles won {threePlayerDice.RollsWon}/{threePlayerDice.RollsTaken}");
            }
        }

        await slack.PostMessageAsync(new SlackMessage(message.ChannelId, sb.ToString().TrimEnd(), ThreadTs: message.ThreadTs), ct);
    }

    private async Task LeaderboardAsync(IncomingMessage message, GameMode mode, CancellationToken ct)
    {
        if (mode is GameMode.ThreePlayer or GameMode.ThreePlayerDice)
        {
            await ThreePlayerLeaderboardAsync(message, withDice: mode == GameMode.ThreePlayerDice, ct);
            return;
        }

        var standings = await StandingsAsync(mode, ct);
        var dice = mode == GameMode.DiceRoll;
        if (standings.Count == 0)
        {
            var none = mode switch
            {
                GameMode.DiceRoll => "No ChessDiceRoll games have been finished yet.",
                GameMode.Chess960 => $"No {Messages.Chess960Name} games have been finished yet.",
                _ => "No games have been finished yet.",
            };
            await slack.PostMessageAsync(new SlackMessage(message.ChannelId, none, ThreadTs: message.ThreadTs), ct);
            return;
        }

        var sb = new StringBuilder(mode switch
        {
            GameMode.DiceRoll => $":game_die: *ChessDiceRoll Leaderboard* ({RatingNote})\n",
            GameMode.Chess960 => $":twisted_rightwards_arrows: *{Messages.Chess960Name} Leaderboard* ({RatingNote})\n",
            _ => $":trophy: *Leaderboard* ({RatingNote})\n",
        });
        var rank = 1;
        foreach (var s in standings.Take(10))
        {
            var gambles = dice ? $" · gambles won {s.RollsWon}/{s.RollsTaken}" : "";
            sb.AppendLine($"{rank++}. <@{s.UserId}> — *{s.Rating:0}* ({s.Wins}W {s.Losses}L {s.Draws}D){gambles}");
        }

        await slack.PostMessageAsync(new SlackMessage(message.ChannelId, sb.ToString(), ThreadTs: message.ThreadTs), ct);
    }

    private static readonly string RatingNote = $"Elo rating, everyone starts at {Elo.StartingRating:0}";

    /// <summary>Results and Elo ratings for one two-player mode. Ratings are replayed from the finished games in the order they ended.</summary>
    private async Task<List<Standing>> StandingsAsync(GameMode mode, CancellationToken ct)
    {
        var table = new Dictionary<string, Standing>();
        Standing For(string user) => table.TryGetValue(user, out var s) ? s : table[user] = new Standing(user);
        var elo = new Elo();

        var finished = (await games.GetFinishedGamesAsync(ct)).OrderBy(g => g.UpdatedAt).ThenBy(g => g.CreatedAt);
        foreach (var record in finished)
        {
            if (record.Termination == Termination.Abandoned || record.Mode != mode)
            {
                continue;
            }

            var white = For(record.WhiteId);
            var black = For(record.BlackId);
            foreach (var roll in record.Dice.Log)
            {
                var roller = For(roll.UserId);
                roller.RollsTaken++;
                roller.RollsWon += roll.Success ? 1 : 0;
            }
            switch (record.Outcome)
            {
                case GameOutcome.WhiteWins:
                    white.Wins++;
                    black.Losses++;
                    break;
                case GameOutcome.BlackWins:
                    black.Wins++;
                    white.Losses++;
                    break;
                case GameOutcome.Draw:
                    white.Draws++;
                    black.Draws++;
                    break;
                default:
                    continue;
            }

            var whiteScore = record.Outcome switch { GameOutcome.WhiteWins => 1, GameOutcome.BlackWins => 0, _ => 0.5 };
            (white.LastChange, black.LastChange) = elo.Record(record.WhiteId, record.BlackId, whiteScore);
            white.Rating = elo.RatingOf(record.WhiteId);
            black.Rating = elo.RatingOf(record.BlackId);
        }

        return table.Values.OrderByDescending(s => s.Rating).ThenByDescending(s => s.Games).ToList();
    }

    private async Task<bool> EnsureActivePlayerAsync(GameRecord record, string userId, Func<string, Task> reply)
    {
        var problem = record.State switch
        {
            GameState.Challenged => $"The game hasn't started — <@{record.OpponentId}> still needs to accept.",
            GameState.Declined => "That challenge was declined.",
            GameState.Finished => "This game is over. Start a new one with `@ChessBetterBot challenge @someone`.",
            _ when !record.IsPlayer(userId) => "You're not playing in this game — but feel free to watch!",
            _ => null,
        };

        if (problem is not null)
        {
            await reply(problem);
            return false;
        }

        return true;
    }

    private async Task PostBoardAsync(GameRecord record, Game game, CancellationToken ct)
    {
        // The board is drawn in the theme and pieces of whoever has to move next.
        var style = await users.GetPreferencesAsync(record.PlayerFor(game.Current.SideToMove), ct);
        var board = Messages.Board(record, game, Options.PublicBaseUrl, style);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, board.Text, board.Blocks, record.ThreadTs), ct);
    }

    private async Task FinishAsync(GameRecord record, Game game, CancellationToken ct)
    {
        // Dice games can't be replayed as normal chess, so lichess gets only the final position (see AnalysisUrl).
        if (Options.ImportFinishedGamesToLichess && game.Moves.Count > 0 && game.Termination != Termination.Abandoned &&
            record.Mode is GameMode.Standard or GameMode.Chess960)
        {
            record.AnalysisUrl = await analysis.ImportAsync(await BuildPgnAsync(record, game, ct), ct);
            if (record.AnalysisUrl is not null)
            {
                await SaveAsync(record, ct);
            }
        }

        await PostGameOverAsync(record, game, broadcast: true, ct, await RatingChangesAsync(record, ct));
        await UpdateCardAsync(record, game, ct);
    }

    /// <summary>"Ratings: @white 1216 (+16) · @black 1184 (−16)" for a game that has just finished, or null if it doesn't count.</summary>
    private async Task<string?> RatingChangesAsync(GameRecord record, CancellationToken ct)
    {
        if (record.Termination == Termination.Abandoned || record.Outcome == GameOutcome.Ongoing)
        {
            return null;
        }

        // The game was saved as finished just now, so it's the latest game for both players.
        var standings = await StandingsAsync(record.Mode, ct);
        string Part(string userId)
        {
            var s = standings.First(x => x.UserId == userId);
            var change = Math.Round(s.LastChange);
            var sign = change > 0 ? "+" : change < 0 ? "−" : "±";
            return $"<@{userId}> *{s.Rating:0}* ({sign}{Math.Abs(change):0})";
        }

        return $":chart_with_upwards_trend: Ratings: {Part(record.WhiteId)} · {Part(record.BlackId)}";
    }

    private async Task PostGameOverAsync(GameRecord record, Game game, bool broadcast, CancellationToken ct, string? ratings = null)
    {
        var pgn = await BuildPgnAsync(record, game, ct);
        var content = Messages.GameOver(record, game, pgn, AnalysisUrl(record, game), Options.PublicBaseUrl, ratings);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, content.Text, content.Blocks, record.ThreadTs, broadcast), ct);
    }

    private async Task UpdateCardAsync(GameRecord record, Game? game, CancellationToken ct)
    {
        var card = Messages.StatusCard(record, game);
        await slack.UpdateMessageAsync(record.ChannelId, record.ThreadTs, card.Text, card.Blocks, ct);
    }

    private async Task<string> BuildPgnAsync(GameRecord record, Game game, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["Event"] = "Slack chess",
            ["Site"] = "Slack",
            ["Date"] = record.CreatedAt.ToString("yyyy.MM.dd"),
            ["White"] = await slack.GetUserNameAsync(record.WhiteId, ct),
            ["Black"] = await slack.GetUserNameAsync(record.BlackId, ct),
        };
        switch (record.Mode)
        {
            case GameMode.DiceRoll:
                headers["Variant"] = "ChessDiceRoll";
                break;
            case GameMode.Chess960:
                // Lichess needs exactly "Chess960" here to import the game; Game.ToPgn adds the starting FEN.
                headers["Variant"] = "Chess960";
                break;
        }

        return game.ToPgn(headers);
    }

    /// <summary>
    /// The imported lichess game if there is one. Otherwise a lichess board: the move list for normal chess, or just
    /// the current position for dice games (which lichess can't replay) and Chess960 (whose start the URL can't carry).
    /// </summary>
    private static string AnalysisUrl(GameRecord record, Game game) =>
        record.AnalysisUrl ?? record.Mode switch
        {
            GameMode.DiceRoll => LichessClient.AnalysisPositionUrl(game.Current.ToFen()),
            GameMode.Chess960 => LichessClient.AnalysisPositionUrl(game.Current.ToFen(), "chess960"),
            _ => LichessClient.AnalysisBoardUrl(game.MovesSan()),
        };

    private Task ThemeAsync(IncomingMessage message, string? name, CancellationToken ct) =>
        PreferenceAsync(message, name, "theme", "board theme",
            Rendering.BoardTheme.All.Select(t => t.Name).ToList(), Rendering.BoardTheme.Default.Name,
            p => p.Theme, users.SetThemeAsync, ct);

    private Task PiecesAsync(IncomingMessage message, string? name, CancellationToken ct) =>
        PreferenceAsync(message, name, "pieces", "piece set",
            Rendering.PieceSet.All.Select(s => s.Name).ToList(), Rendering.PieceSet.Default.Name,
            p => p.Pieces, users.SetPiecesAsync, ct);

    /// <summary>Shows a board preference (when <paramref name="name"/> is null) or sets it to one of <paramref name="choices"/>.</summary>
    private async Task PreferenceAsync(
        IncomingMessage message,
        string? name,
        string command,
        string label,
        IReadOnlyList<string> choices,
        string defaultChoice,
        Func<UserPreferences, string?> current,
        Func<string, string, CancellationToken, Task> save,
        CancellationToken ct)
    {
        var options = string.Join(", ", choices.Select(c => $"`{c}`"));
        if (name is null)
        {
            var mine = current(await users.GetPreferencesAsync(message.UserId, ct)) ?? defaultChoice;
            await EphemeralAsync(message, $"Your {label} is *{mine}*. Change it with `@ChessBetterBot {command} <name>`: {options}.", ct);
            return;
        }

        var choice = choices.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            await EphemeralAsync(message, $"There's no `{name}` {label}. Pick one of {options}.", ct);
            return;
        }

        await save(message.UserId, choice, ct);
        await EphemeralAsync(message, $"Your {label} is now *{choice}*. You'll see it on the board whenever it's your turn.", ct);
    }

    private Task SaveAsync(GameRecord record, CancellationToken ct)
    {
        record.UpdatedAt = time.GetUtcNow();
        return games.SaveAsync(record, ct);
    }

    private Task EphemeralAsync(IncomingMessage message, string text, CancellationToken ct) =>
        slack.PostEphemeralAsync(message.ChannelId, message.UserId, message.ThreadTs, text, ct);

    private sealed class Standing(string userId)
    {
        public string UserId { get; } = userId;

        public int Wins { get; set; }

        public int Losses { get; set; }

        public int Draws { get; set; }

        public int RollsTaken { get; set; }

        public int RollsWon { get; set; }

        public int Games => Wins + Losses + Draws;

        public double Rating { get; set; } = Elo.StartingRating;

        /// <summary>How much <see cref="Rating"/> moved in this player's most recent game.</summary>
        public double LastChange { get; set; }
    }
}

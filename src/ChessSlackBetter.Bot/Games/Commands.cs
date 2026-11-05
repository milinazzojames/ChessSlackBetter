using System.Text.RegularExpressions;
using ChessSlackBetter.Chess;
using ChessSlackBetter.Chess.ThreePlayer;

namespace ChessSlackBetter.Bot.Games;

public abstract record BotCommand;

public sealed record HelpCommand : BotCommand;

/// <summary>Challenge another user. <see cref="ChallengerColor"/> is null for a random colour.</summary>
public sealed record ChallengeCommand(string OpponentId, PieceColor? ChallengerColor, GameMode Mode = GameMode.Standard) : BotCommand;

/// <summary>Challenge two other users to three-player chess, optionally with dice. Seats are drawn at random.</summary>
public sealed record ThreePlayerChallengeCommand(string FirstOpponentId, string SecondOpponentId, bool WithDice = false) : BotCommand;

/// <summary>A dice-game gamble described in plain English.</summary>
public sealed record RollRequestCommand(string Request) : BotCommand;

/// <summary>The requester agrees to roll for their pending gamble.</summary>
public sealed record ConfirmRollCommand : BotCommand;

/// <summary>The requester backs out of their pending gamble.</summary>
public sealed record CancelRollCommand : BotCommand;

/// <summary>The opponent blocks the pending gamble.</summary>
public sealed record VetoCommand : BotCommand;

public sealed record AcceptCommand : BotCommand;

public sealed record DeclineCommand : BotCommand;

public sealed record MoveCommand(string Move) : BotCommand;

public sealed record ResignCommand : BotCommand;

public sealed record DrawCommand : BotCommand;

public sealed record DeclineDrawCommand : BotCommand;

public sealed record AbortCommand : BotCommand;

/// <summary>Take back your last move before your opponent replies. Once per player per normal game.</summary>
public sealed record UndoCommand : BotCommand;

public sealed record BoardCommand : BotCommand;

public sealed record PgnCommand : BotCommand;

public sealed record GamesCommand : BotCommand;

/// <summary>Win/loss record for a user; <see cref="UserId"/> is null for the person asking.</summary>
public sealed record StatsCommand(string? UserId) : BotCommand;

public sealed record LeaderboardCommand(GameMode Mode = GameMode.Standard) : BotCommand;

/// <summary>Set the asker's board theme; <see cref="Name"/> is null to show the current one and the choices.</summary>
public sealed record ThemeCommand(string? Name) : BotCommand;

/// <summary>Set the asker's piece set; <see cref="Name"/> is null to show the current one and the choices.</summary>
public sealed record PiecesCommand(string? Name) : BotCommand;

public sealed record UnknownCommand(string Text) : BotCommand;

public static partial class CommandParser
{
    [GeneratedRegex(@"<@([A-Z0-9]+)(?:\|[^>]*)?>")]
    private static partial Regex Mention();

    /// <summary>Parses the text of a message after the bot's own mention has been removed.</summary>
    public static BotCommand Parse(string text)
    {
        var trimmed = text.Trim();
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return new HelpCommand();
        }

        var verb = words[0].ToLowerInvariant().TrimEnd('!', '.', '?');
        var mentioned = Mention().Matches(trimmed).Select(m => m.Groups[1].Value).FirstOrDefault();

        switch (verb)
        {
            case "help" or "commands" or "?":
                return new HelpCommand();
            case "threeplayerchess" or "threeplayer" or "3pchess" or "3p" or "threeplayerdice" or "threeplayerdiceroll" or "3pdice":
                var opponents = Mention().Matches(trimmed).Select(m => m.Groups[1].Value).Distinct().ToList();
                return opponents.Count == 2
                    ? new ThreePlayerChallengeCommand(opponents[0], opponents[1],
                        verb.Contains("dice") || words.Any(w => w.StartsWith("dice", StringComparison.OrdinalIgnoreCase)))
                    : new UnknownCommand(trimmed);
            case "challenge" or "play" or "duel" or "vs" or "diceroll" or "chessdiceroll" or "dice" or
                "chess960" or "960" or "fischerrandom" or "fischer" or "freestyle":
                if (mentioned is null)
                {
                    return new UnknownCommand(trimmed);
                }

                PieceColor? color = null;
                if (words.Any(w => w.Equals("white", StringComparison.OrdinalIgnoreCase))) color = PieceColor.White;
                if (words.Any(w => w.Equals("black", StringComparison.OrdinalIgnoreCase))) color = PieceColor.Black;
                var mode = verb is "diceroll" or "chessdiceroll" or "dice" ||
                           words.Any(w => w.Equals("diceroll", StringComparison.OrdinalIgnoreCase))
                    ? GameMode.DiceRoll
                    : verb is "chess960" or "960" or "fischerrandom" or "fischer" or "freestyle" ||
                      words.Any(w => w is "960" || w.Equals("chess960", StringComparison.OrdinalIgnoreCase))
                        ? GameMode.Chess960
                        : GameMode.Standard;
                return new ChallengeCommand(mentioned, color, mode);
            case "roll" or "gamble" when words.Length > 1:
                return new RollRequestCommand(trimmed[words[0].Length..].Trim());
            case "roll" or "yes" or "y" or "yep" or "yeah" or "sure":
                return new ConfirmRollCommand();
            case "no" or "n" or "nope" or "nah":
                return new CancelRollCommand();
            case "veto":
                return new VetoCommand();
            case "accept":
                return new AcceptCommand();
            case "decline" or "reject" or "cancel" or "withdraw":
                return words.Length > 1 && words[1].Equals("draw", StringComparison.OrdinalIgnoreCase)
                    ? new DeclineDrawCommand()
                    : new DeclineCommand();
            case "resign" or "forfeit" or "gg":
                return new ResignCommand();
            case "draw" or "offer":
                return new DrawCommand();
            case "abort":
                return new AbortCommand();
            case "undo" or "takeback" or "oops":
                return new UndoCommand();
            case "board" or "show" or "status":
                return new BoardCommand();
            case "pgn" or "analyze" or "analysis":
                return new PgnCommand();
            case "games" or "list":
                return new GamesCommand();
            case "stats" or "record":
                return new StatsCommand(mentioned);
            case "leaderboard" or "top" or "rankings":
                var board = words.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "";
                return new LeaderboardCommand(
                    board.StartsWith("dice") ? GameMode.DiceRoll :
                    board is "960" or "chess960" or "fischer" or "fischerrandom" or "freestyle" ? GameMode.Chess960 :
                    board is "3pdice" or "threeplayerdice" ? GameMode.ThreePlayerDice :
                    board is "3p" or "three" or "threeplayer" or "threeplayerchess" ? GameMode.ThreePlayer :
                    GameMode.Standard);
            case "theme" or "colours" or "colors":
                return new ThemeCommand(words.Length > 1 ? words[1] : null);
            case "pieces" or "piece":
                return new PiecesCommand(words.Length > 1 ? words[1] : null);
            case "move" or "m" when words.Length > 1:
                return new MoveCommand(words[1]);
        }

        if (words.Length == 1 && (Notation.LooksLikeMove(words[0]) || TriNotation.LooksLikeMove(words[0])))
        {
            return new MoveCommand(words[0]);
        }

        return new UnknownCommand(trimmed);
    }

    public static string StripMention(string text, string userId) =>
        Regex.Replace(text, $@"<@{Regex.Escape(userId)}(?:\|[^>]*)?>", "").Trim();
}

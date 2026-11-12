using ChessSlackBetter.Chess;

namespace ChessSlackBetter.Bot.Games;

public enum GameState
{
    /// <summary>Waiting for the challenged player to accept.</summary>
    Challenged,
    Active,
    Finished,

    /// <summary>The challenge was declined or withdrawn; no game was played.</summary>
    Declined,
}

public enum GameMode
{
    Standard,

    /// <summary>ChessDiceRoll: players may gamble on a d100 instead of moving.</summary>
    DiceRoll,

    /// <summary>Three-player chess. These games are <see cref="ThreePlayerRecord"/>s, never <see cref="GameRecord"/>s.</summary>
    ThreePlayer,

    /// <summary>Three-player chess with dice gambles. Also a <see cref="ThreePlayerRecord"/>.</summary>
    ThreePlayerDice,

    /// <summary>Chess960 (Fischer Random): normal rules from a shuffled back rank, see <see cref="GameRecord.Chess960Number"/>.</summary>
    Chess960,
}

/// <summary>
/// A game as stored in the database. Each game lives in the Slack thread under the bot's challenge message,
/// so (ChannelId, ThreadTs) identifies it.
/// </summary>
public sealed class GameRecord
{
    public required string ChannelId { get; init; }

    public required string ThreadTs { get; init; }

    public GameMode Mode { get; init; } = GameMode.Standard;

    /// <summary>Which of the 960 starting positions a <see cref="GameMode.Chess960"/> game uses; null for other modes.</summary>
    public int? Chess960Number { get; init; }

    /// <summary>Rolls, vetoes and the pending gamble. Only used in <see cref="GameMode.DiceRoll"/> games.</summary>
    public Dice.DiceState Dice { get; set; } = new();

    public required string ChallengerId { get; init; }

    public required string OpponentId { get; init; }

    public required string WhiteId { get; init; }

    public required string BlackId { get; init; }

    public GameState State { get; set; } = GameState.Challenged;

    /// <summary>Players who have used their one undo. Only <see cref="GameMode.Standard"/> games allow undos.</summary>
    public List<string> UndosUsedBy { get; set; } = [];

    /// <summary>Moves in UCI notation, e.g. "e2e4", plus "dice:..." entries for dice rolls.</summary>
    public List<string> Moves { get; set; } = [];

    public string? DrawOfferedBy { get; set; }

    public GameOutcome Outcome { get; set; } = GameOutcome.Ongoing;

    public Termination Termination { get; set; } = Termination.None;

    /// <summary>Lichess analysis URL, set once a finished game has been imported.</summary>
    public string? AnalysisUrl { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsPlayer(string userId) => userId == WhiteId || userId == BlackId;

    public PieceColor ColorOf(string userId) =>
        userId == WhiteId ? PieceColor.White :
        userId == BlackId ? PieceColor.Black :
        throw new InvalidOperationException($"{userId} is not playing in this game.");

    public string PlayerFor(PieceColor color) => color == PieceColor.White ? WhiteId : BlackId;

    public string OpponentOf(string userId) => userId == WhiteId ? BlackId : WhiteId;

    /// <summary>Replays the stored moves, including any result that isn't implied by the moves themselves.</summary>
    public Game LoadGame()
    {
        var game = Game.Replay(Moves, Chess960Number is { } number ? Chess.Chess960.StartPosition(number) : null);
        if (!game.IsOver)
        {
            switch (Termination)
            {
                case Termination.Resignation:
                    game.Resign(Outcome == GameOutcome.WhiteWins ? PieceColor.Black : PieceColor.White);
                    break;
                case Termination.DrawAgreement:
                    game.AgreeDraw();
                    break;
                case Termination.Abandoned:
                    game.Abandon();
                    break;
            }
        }

        return game;
    }

    /// <summary>Copies the result of a game into this record.</summary>
    public void SyncFrom(Game game)
    {
        Moves = game.Moves.Select(m => m.ToToken()).ToList();
        Outcome = game.Outcome;
        Termination = game.Termination;
        if (game.IsOver)
        {
            State = GameState.Finished;
            DrawOfferedBy = null;
            Dice.Pending = null;
        }
    }
}

using ChessSlackBetter.Chess.ThreePlayer;

namespace ChessSlackBetter.Bot.Games;

/// <summary>
/// A three-player game as stored in the database. Like two-player games it lives in the thread under the bot's
/// challenge message. All three players must accept before it starts; the challenger accepts by challenging.
/// </summary>
public sealed class ThreePlayerRecord
{
    public required string ChannelId { get; init; }

    public required string ThreadTs { get; init; }

    public required string ChallengerId { get; init; }

    public required string WhiteId { get; init; }

    public required string BlackId { get; init; }

    public required string RedId { get; init; }

    /// <summary>Three-player ChessDiceRoll: players may gamble on a d100 instead of moving.</summary>
    public bool WithDice { get; init; }

    /// <summary>Rolls, vetoes and the pending gamble. Only used when <see cref="WithDice"/> is set.</summary>
    public Dice.DiceState Dice { get; set; } = new();

    public GameState State { get; set; } = GameState.Challenged;

    /// <summary>Players who have accepted the challenge.</summary>
    public List<string> Accepted { get; set; } = [];

    /// <summary>Who declined or withdrew, once <see cref="State"/> is <see cref="GameState.Declined"/>.</summary>
    public string? DeclinedBy { get; set; }

    /// <summary>Stored <see cref="TriGame.Tokens"/>: moves, resignations and the draw or abort that ended it.</summary>
    public List<string> Moves { get; set; } = [];

    /// <summary>Players still in the game who have agreed to a draw. It's agreed once all of them have.</summary>
    public List<string> DrawAgreed { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IEnumerable<string> Players => [WhiteId, BlackId, RedId];

    public bool IsPlayer(string userId) => Players.Contains(userId);

    public TriColor ColorOf(string userId) =>
        userId == WhiteId ? TriColor.White :
        userId == BlackId ? TriColor.Black :
        userId == RedId ? TriColor.Red :
        throw new InvalidOperationException($"{userId} is not playing in this game.");

    public string PlayerFor(TriColor color) => color switch
    {
        TriColor.White => WhiteId,
        TriColor.Black => BlackId,
        _ => RedId,
    };

    public TriGame LoadGame() => TriGame.Replay(Moves);

    public void SyncFrom(TriGame game)
    {
        Moves = game.Tokens.ToList();
        DrawAgreed.RemoveAll(u => game.IsEliminated(ColorOf(u)));
        if (game.IsOver)
        {
            State = GameState.Finished;
            DrawAgreed.Clear();
            Dice.Pending = null;
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ChessSlackBetter.Chess;

public enum GameOutcome
{
    Ongoing,
    WhiteWins,
    BlackWins,
    Draw,
}

public enum Termination
{
    None,
    Checkmate,
    Resignation,
    Stalemate,
    DrawAgreement,
    ThreefoldRepetition,
    FiftyMoveRule,
    InsufficientMaterial,
    Abandoned,
}

/// <summary>
/// One turn of the game: a normal move, or (in dice games) a set of board edits in place of a move,
/// in which case <see cref="Edits"/> is set and <see cref="Move"/> is meaningless.
/// </summary>
/// <param name="After">
/// Board edits made after the turn without passing it, by passive dice rules that fire at the end of a turn.
/// </param>
public sealed record PlayedMove(Move Move, string San, IReadOnlyList<SquareEdit>? Edits = null, IReadOnlyList<SquareEdit>? After = null)
{
    public const string DicePrefix = "dice:";

    /// <summary>Separates a turn's token from the edits passive rules made after it.</summary>
    public const char AfterSeparator = '|';

    public bool IsDiceRoll => Edits is not null;

    /// <summary>
    /// Storage form: UCI for moves ("e2e4"), "dice:e4=B,d5=-" for edits, followed by "|c5=P" when rules fired after.
    /// </summary>
    public string ToToken() =>
        (Edits is null ? Move.ToUci() : DicePrefix + string.Join(',', Edits)) +
        (After is { Count: > 0 } ? AfterSeparator + string.Join(',', After) : "");

    /// <summary>The SAN plus, when rules fired after the turn, a comment such as "{rule c5=P}".</summary>
    public string SanWithRules =>
        After is { Count: > 0 } ? San + " {rule " + string.Join(' ', After) + "}" : San;
}

/// <summary>A game from a starting position (normally the standard one): its moves and how (or whether) it ended.</summary>
public sealed class Game
{
    private readonly List<PlayedMove> _moves = [];
    private readonly List<string> _repetitionKeys = [];

    /// <param name="start">Defaults to the standard starting position; Chess960 games pass theirs.</param>
    public Game(Position? start = null)
    {
        Start = start ?? Position.Start;
        Current = Start;
        _repetitionKeys.Add(Current.RepetitionKey());
    }

    public Position Start { get; }

    public Position Current { get; private set; }

    public IReadOnlyList<PlayedMove> Moves => _moves;

    public PlayedMove? LastMove => _moves.Count == 0 ? null : _moves[^1];

    public GameOutcome Outcome { get; private set; } = GameOutcome.Ongoing;

    public Termination Termination { get; private set; } = Termination.None;

    public bool IsOver => Outcome != GameOutcome.Ongoing;

    /// <summary>Rebuilds a game from stored tokens (see <see cref="PlayedMove.ToToken"/>), e.g. "e2e4", "dice:e4=B".</summary>
    public static Game Replay(IEnumerable<string> tokens, Position? start = null)
    {
        var game = new Game(start);
        foreach (var stored in tokens)
        {
            var parts = stored.Split(PlayedMove.AfterSeparator);
            var token = parts[0];
            string? error;
            var ok = token.StartsWith(PlayedMove.DicePrefix, StringComparison.Ordinal)
                ? game.TryApplyEdits(ParseEdits(token[PlayedMove.DicePrefix.Length..]), out error)
                : game.TryPlay(token, out _, out error);
            if (ok && parts.Length > 1)
            {
                ok = game.TryApplyAfterTurn(ParseEdits(parts[1]), out error);
            }

            if (!ok)
            {
                throw new FormatException($"Stored move '{stored}' is invalid: {error}");
            }
        }

        return game;
    }

    private static List<SquareEdit> ParseEdits(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(SquareEdit.Parse).ToList();

    /// <summary>
    /// Checks edits a passive rule wants to make after a turn, with the same side still to move: kings can't be
    /// touched, pawns can't sit on their promotion rank, the player who just moved can't be left in check, and the
    /// player to move must still have a legal move.
    /// </summary>
    public static bool ValidateAfterTurnEdits(Position position, IReadOnlyList<SquareEdit> edits, [NotNullWhen(false)] out string? error)
    {
        if (!ValidatePieces(position, edits, out error))
        {
            return false;
        }

        var after = position.WithEdits(edits, passTurn: false);
        if (after.IsInCheck(position.SideToMove.Opponent()))
        {
            error = "That would leave the king of the player who just moved in check.";
            return false;
        }

        if (after.GenerateLegalMoves().Count == 0)
        {
            error = after.IsInCheck() ? "Dice can't deliver checkmate." : "That would stalemate the player to move.";
            return false;
        }

        return true;
    }

    /// <summary>Applies a passive rule's edits after the last turn; the same side stays to move.</summary>
    public bool TryApplyAfterTurn(IReadOnlyList<SquareEdit> edits, [NotNullWhen(false)] out string? error)
    {
        if (IsOver || _moves.Count == 0)
        {
            error = IsOver ? "The game is already over." : "Rules can only fire after a turn.";
            return false;
        }

        if (!ValidateAfterTurnEdits(Current, edits, out error))
        {
            return false;
        }

        var last = _moves[^1];
        _moves[^1] = last with { After = [.. last.After ?? [], .. edits] };
        Current = Current.WithEdits(edits, passTurn: false);
        _repetitionKeys[^1] = Current.RepetitionKey();
        DetectAutomaticEnd();
        return true;
    }

    /// <summary>
    /// Checks whether board edits may replace the current player's move: kings can't be touched or created,
    /// pawns can't land on the back ranks, the player can't leave themself in check, and the opponent must
    /// still have a legal move (no checkmate or stalemate by dice). No edits at all is fine: the turn just passes.
    /// </summary>
    public static bool ValidateEdits(Position position, IReadOnlyList<SquareEdit> edits, [NotNullWhen(false)] out string? error)
    {
        if (!ValidatePieces(position, edits, out error))
        {
            return false;
        }

        var after = position.WithEdits(edits);
        if (after.IsInCheck(position.SideToMove))
        {
            error = "That would leave your own king in check.";
            return false;
        }

        if (after.GenerateLegalMoves().Count == 0)
        {
            error = after.IsInCheck()
                ? "Dice can't deliver checkmate."
                : "That would stalemate your opponent.";
            return false;
        }

        return true;
    }

    private static bool ValidatePieces(Position position, IReadOnlyList<SquareEdit> edits, [NotNullWhen(false)] out string? error)
    {
        error = null;
        foreach (var edit in edits)
        {
            if (position[edit.Square].Type == PieceType.King || edit.Piece.Type == PieceType.King)
            {
                error = "Kings can't be changed by dice.";
                return false;
            }

            // A pawn on its own back rank can still move forward; one on its promotion rank can't move at all.
            var promotionRank = edit.Piece.Color == PieceColor.White ? 7 : 0;
            if (edit.Piece.Type == PieceType.Pawn && Square.Rank(edit.Square) == promotionRank)
            {
                error = "Pawns can't be placed on the rank where they would promote.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Applies dice edits in place of the current player's move.</summary>
    public bool TryApplyEdits(IReadOnlyList<SquareEdit> edits, [NotNullWhen(false)] out string? error)
    {
        if (IsOver)
        {
            error = "The game is already over.";
            return false;
        }

        if (!ValidateEdits(Current, edits, out error))
        {
            return false;
        }

        _moves.Add(new PlayedMove(default, "{" + string.Join(' ', edits.Select(e => e.ToString()).Prepend("dice")) + "}", edits));
        Current = Current.WithEdits(edits);
        _repetitionKeys.Add(Current.RepetitionKey());
        DetectAutomaticEnd();
        return true;
    }

    public bool TryPlay(string input, [NotNullWhen(true)] out PlayedMove? played, [NotNullWhen(false)] out string? error)
    {
        played = null;
        if (IsOver)
        {
            error = "The game is already over.";
            return false;
        }

        if (!Notation.TryParseMove(Current, input, out var move, out error))
        {
            return false;
        }

        played = new PlayedMove(move, Notation.ToSan(Current, move));
        _moves.Add(played);
        Current = Current.Apply(move);
        _repetitionKeys.Add(Current.RepetitionKey());
        DetectAutomaticEnd();
        return true;
    }

    public void Resign(PieceColor loser) =>
        End(loser == PieceColor.White ? GameOutcome.BlackWins : GameOutcome.WhiteWins, Termination.Resignation);

    public void AgreeDraw() => End(GameOutcome.Draw, Termination.DrawAgreement);

    public void Abandon() => End(GameOutcome.Draw, Termination.Abandoned);

    public string ResultString => Outcome switch
    {
        GameOutcome.WhiteWins => "1-0",
        GameOutcome.BlackWins => "0-1",
        GameOutcome.Draw => "1/2-1/2",
        _ => "*",
    };

    public string MovesSan()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _moves.Count; i++)
        {
            if (i % 2 == 0)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(i / 2 + 1).Append(". ");
            }
            else
            {
                sb.Append(' ');
            }

            sb.Append(_moves[i].SanWithRules);
        }

        return sb.ToString();
    }

    public string ToPgn(IReadOnlyDictionary<string, string>? headers = null)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            sb.Append('[').Append(key).Append(" \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).AppendLine("\"]");
        }

        if (Start.ToFen() != Position.StartFen)
        {
            sb.AppendLine("[SetUp \"1\"]");
            sb.Append("[FEN \"").Append(Start.ToFen()).AppendLine("\"]");
        }

        sb.Append("[Result \"").Append(ResultString).AppendLine("\"]");
        sb.AppendLine();
        var moves = MovesSan();
        sb.Append(moves.Length == 0 ? ResultString : moves + " " + ResultString);
        return sb.ToString();
    }

    private void DetectAutomaticEnd()
    {
        var hasMoves = Current.GenerateLegalMoves().Count > 0;
        if (!hasMoves)
        {
            if (Current.IsInCheck())
            {
                End(Current.SideToMove == PieceColor.White ? GameOutcome.BlackWins : GameOutcome.WhiteWins, Termination.Checkmate);
            }
            else
            {
                End(GameOutcome.Draw, Termination.Stalemate);
            }
        }
        else if (Current.IsInsufficientMaterial())
        {
            End(GameOutcome.Draw, Termination.InsufficientMaterial);
        }
        else if (_repetitionKeys.Count(k => k == _repetitionKeys[^1]) >= 3)
        {
            End(GameOutcome.Draw, Termination.ThreefoldRepetition);
        }
        else if (Current.HalfmoveClock >= 100)
        {
            End(GameOutcome.Draw, Termination.FiftyMoveRule);
        }
    }

    private void End(GameOutcome outcome, Termination termination)
    {
        if (IsOver)
        {
            throw new InvalidOperationException("The game is already over.");
        }

        Outcome = outcome;
        Termination = termination;
    }
}

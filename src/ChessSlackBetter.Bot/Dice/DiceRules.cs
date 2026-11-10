using System.Diagnostics.CodeAnalysis;
using ChessSlackBetter.Chess;

namespace ChessSlackBetter.Bot.Dice;

/// <summary>The deterministic half of the referee: turning effects into board edits and pricing the gamble.</summary>
public static class DiceRules
{
    /// <summary>Largest positional adjustment (in pawns) the referee may add on top of material.</summary>
    public const double MaxPositionalBonus = 3;

    public static int PieceValue(PieceType type) => type switch
    {
        PieceType.Pawn => 1,
        PieceType.Knight or PieceType.Bishop => 3,
        PieceType.Rook => 5,
        PieceType.Queen => 9,
        _ => 0,
    };

    /// <summary>A random empty square (never rank 1 or 8 when a pawn is placed there).</summary>
    public const string RandomEmpty = "random_empty";

    /// <summary>A random white piece other than the king.</summary>
    public const string RandomWhite = "random_white";

    /// <summary>A random black piece other than the king.</summary>
    public const string RandomBlack = "random_black";

    /// <summary>A random piece of either colour other than a king.</summary>
    public const string RandomPiece = "random_piece";

    /// <summary>
    /// Applies the effects in order to a copy of the board and returns the squares that changed. Squares may be
    /// random selectors (<see cref="RandomEmpty"/> and friends), resolved with <paramref name="pick"/>, which
    /// returns a uniformly random index below its argument.
    /// </summary>
    public static bool TryBuildEdits(
        Position position,
        IReadOnlyList<DiceEffect> effects,
        [NotNullWhen(true)] out List<SquareEdit>? edits,
        [NotNullWhen(false)] out string? error,
        Func<int, int>? pick = null)
    {
        pick ??= Random.Shared.Next;
        edits = null;
        var board = Enumerable.Range(0, 64).Select(sq => position[sq]).ToArray();

        foreach (var effect in effects)
        {
            if (effect.Type.Equals(JimmyGambit, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseColor(effect.Color, out var side))
                {
                    error = "A Jimmy gambit needs the colour of the side sacrificing.";
                    return false;
                }

                ApplyJimmyGambit(board, side);
                continue;
            }

            var placesPawn = effect.Type.Equals("add", StringComparison.OrdinalIgnoreCase) &&
                             effect.Piece.Equals("pawn", StringComparison.OrdinalIgnoreCase);
            if (!TryResolveSquare(board, effect.Square, placesPawn, pick, out var square, out error))
            {
                return false;
            }

            var current = board[square];
            switch (effect.Type.ToLowerInvariant())
            {
                case "transform":
                    if (current.IsEmpty)
                    {
                        error = $"There's no piece on {effect.Square} to transform.";
                        return false;
                    }

                    if (!TryParsePiece(effect.Piece, out var newType, out error))
                    {
                        return false;
                    }

                    var color = TryParseColor(effect.Color, out var c) ? c : current.Color;
                    board[square] = new Piece(newType, color);
                    break;

                case "remove":
                    if (current.IsEmpty)
                    {
                        error = $"There's no piece on {effect.Square} to remove.";
                        return false;
                    }

                    board[square] = Piece.Empty;
                    break;

                case "add":
                    if (!current.IsEmpty)
                    {
                        error = $"{effect.Square} is already occupied.";
                        return false;
                    }

                    if (!TryParsePiece(effect.Piece, out var addType, out error))
                    {
                        return false;
                    }

                    if (!TryParseColor(effect.Color, out var addColor))
                    {
                        error = "An added piece needs a colour.";
                        return false;
                    }

                    board[square] = new Piece(addType, addColor);
                    break;

                case "move":
                    if (current.IsEmpty)
                    {
                        error = $"There's no piece on {effect.Square} to move.";
                        return false;
                    }

                    var emptied = (Piece[])board.Clone();
                    emptied[square] = Piece.Empty;
                    if (!TryResolveSquare(emptied, effect.ToSquare, current.Type == PieceType.Pawn, pick, out var to, out error))
                    {
                        return false;
                    }

                    board[to] = current;
                    board[square] = Piece.Empty;
                    break;

                default:
                    error = $"Unknown effect `{effect.Type}`.";
                    return false;
            }
        }

        edits = Enumerable.Range(0, 64)
            .Where(sq => board[sq] != position[sq])
            .Select(sq => new SquareEdit(sq, board[sq]))
            .ToList();
        error = null;
        return true;
    }

    private static bool TryResolveSquare(
        Piece[] board,
        string text,
        bool placesPawn,
        Func<int, int> pick,
        out int square,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (Square.TryParse(text, out square))
        {
            return true;
        }

        bool NotKing(int sq) => !board[sq].IsEmpty && board[sq].Type != PieceType.King;
        var candidates = Enumerable.Range(0, 64).Where(text.ToLowerInvariant() switch
        {
            RandomEmpty => sq => board[sq].IsEmpty && !(placesPawn && Square.Rank(sq) is 0 or 7),
            RandomWhite => sq => NotKing(sq) && board[sq].Color == PieceColor.White,
            RandomBlack => sq => NotKing(sq) && board[sq].Color == PieceColor.Black,
            RandomPiece => NotKing,
            _ => _ => false,
        }).ToList();

        if (candidates.Count == 0)
        {
            error = text.ToLowerInvariant() is RandomEmpty or RandomWhite or RandomBlack or RandomPiece
                ? $"There's no square to pick for `{text}`."
                : $"`{text}` isn't a square.";
            return false;
        }

        square = candidates[pick(candidates.Count)];
        return true;
    }

    /// <summary>
    /// Rough value of a passive rule to <paramref name="player"/>, in pawns: the average material one firing
    /// gains (sampled, since squares may be random) times the firings expected over the next stretch of the game.
    /// </summary>
    public static double EstimateRuleValue(Position position, DiceRuleSpec rule, PieceColor player)
    {
        const int samples = 24;
        var random = new Random(12345);
        var deltas = new List<int>();
        for (var i = 0; i < samples; i++)
        {
            if (TryBuildEdits(position, rule.Effects, out var edits, out _, random.Next))
            {
                deltas.Add(MaterialDelta(position, edits, player));
            }
        }

        if (deltas.Count == 0)
        {
            return 0;
        }

        // Look ahead about ten of each player's turns; that's most of a typical dice game.
        var turns = rule.Trigger == RuleTriggers.EveryTurn ? 20.0 : 10.0;
        var firings = turns / Math.Max(1, rule.Every);
        if (rule.Times > 0)
        {
            firings = Math.Min(firings, rule.Times);
        }

        return deltas.Average() * firings;
    }

    /// <summary>Effect type for the Jimmy gambit (see <see cref="ApplyJimmyGambit"/>).</summary>
    public const string JimmyGambit = "jimmy_gambit";

    private static readonly (int File, int Rank)[] Orthogonals = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static readonly (int File, int Rank)[] Diagonals = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>
    /// The Jimmy gambit: <paramref name="side"/> sacrifices every piece except its king and the pieces shielding
    /// that king from check. A piece is a shield when it is the nearest piece to the king along a line and the next
    /// enemy piece behind it on that line is a rook, bishop or queen that would attack the king along it once the
    /// sacrificed pieces are gone. Knight and pawn checks can't be blocked, so they never keep a piece.
    /// </summary>
    public static void ApplyJimmyGambit(Piece[] board, PieceColor side)
    {
        var king = Array.FindIndex(board, p => p.Type == PieceType.King && p.Color == side);
        var keep = new HashSet<int>();
        if (king >= 0)
        {
            foreach (var (dir, diagonal) in Orthogonals.Select(d => (d, false)).Concat(Diagonals.Select(d => (d, true))))
            {
                if (ShieldOnLine(board, king, dir, diagonal, side) is { } shield)
                {
                    keep.Add(shield);
                }
            }
        }

        for (var sq = 0; sq < 64; sq++)
        {
            if (!board[sq].IsEmpty && board[sq].Color == side && board[sq].Type != PieceType.King && !keep.Contains(sq))
            {
                board[sq] = Piece.Empty;
            }
        }
    }

    private static int? ShieldOnLine(Piece[] board, int king, (int File, int Rank) dir, bool diagonal, PieceColor side)
    {
        int? shield = null;
        var (file, rank) = (Square.File(king) + dir.File, Square.Rank(king) + dir.Rank);
        for (; Square.IsOnBoard(file, rank); file += dir.File, rank += dir.Rank)
        {
            var piece = board[Square.At(file, rank)];
            if (piece.IsEmpty)
            {
                continue;
            }

            if (piece.Color == side)
            {
                // The nearest own piece is the candidate shield; own pieces beyond it are sacrificed, so look past them.
                shield ??= Square.At(file, rank);
                continue;
            }

            var slides = piece.Type == PieceType.Queen || piece.Type == (diagonal ? PieceType.Bishop : PieceType.Rook);
            return slides ? shield : null;
        }

        return null;
    }

    /// <summary>Change in (own material − opponent material) for <paramref name="player"/>.</summary>
    public static int MaterialDelta(Position position, IEnumerable<SquareEdit> edits, PieceColor player)
    {
        var delta = 0;
        foreach (var edit in edits)
        {
            delta -= SignedValue(position[edit.Square], player);
            delta += SignedValue(edit.Piece, player);
        }

        return delta;
    }

    /// <summary>
    /// The minimum d20 roll that makes the gamble fair: succeeding with probability p is fair when
    /// p × gain = (1 − p) × loss, i.e. p = loss / (gain + loss).
    /// </summary>
    public static int FairTarget(double gain, double loss)
    {
        var p = loss / (gain + loss);
        return (int)Math.Clamp(Math.Round(21 - 20 * p, MidpointRounding.AwayFromZero), 2, 20);
    }

    public static int SuccessPercent(int target) => (21 - target) * 5;

    private static int SignedValue(Piece piece, PieceColor player) =>
        piece.IsEmpty ? 0 : piece.Color == player ? PieceValue(piece.Type) : -PieceValue(piece.Type);

    private static bool TryParsePiece(string text, out PieceType type, [NotNullWhen(false)] out string? error)
    {
        type = text.ToLowerInvariant() switch
        {
            "pawn" => PieceType.Pawn,
            "knight" => PieceType.Knight,
            "bishop" => PieceType.Bishop,
            "rook" => PieceType.Rook,
            "queen" => PieceType.Queen,
            _ => PieceType.None,
        };
        error = type == PieceType.None ? $"`{text}` isn't a piece dice can create." : null;
        return error is null;
    }

    private static bool TryParseColor(string text, out PieceColor color)
    {
        color = text.Equals("black", StringComparison.OrdinalIgnoreCase) ? PieceColor.Black : PieceColor.White;
        return text.Equals("white", StringComparison.OrdinalIgnoreCase) || text.Equals("black", StringComparison.OrdinalIgnoreCase);
    }
}

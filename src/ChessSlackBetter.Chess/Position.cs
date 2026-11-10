using System.Text;

namespace ChessSlackBetter.Chess;

/// <summary>
/// An immutable chess position. <see cref="Apply"/> returns a new position rather than mutating this one.
/// </summary>
public sealed class Position
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static readonly (int File, int Rank)[] KnightOffsets =
        [(1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2)];

    private static readonly (int File, int Rank)[] KingOffsets =
        [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    private static readonly (int File, int Rank)[] RookDirections = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static readonly (int File, int Rank)[] BishopDirections = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    private static readonly PieceType[] PromotionPieces =
        [PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight];

    private readonly Piece[] _board;

    private Position(
        Piece[] board,
        PieceColor sideToMove,
        CastlingRights castling,
        int enPassantSquare,
        int halfmoveClock,
        int fullmoveNumber,
        CastlingLayout layout)
    {
        _board = board;
        SideToMove = sideToMove;
        Castling = castling;
        EnPassantSquare = enPassantSquare;
        HalfmoveClock = halfmoveClock;
        FullmoveNumber = fullmoveNumber;
        Layout = layout;
    }

    public static Position Start { get; } = FromFen(StartFen);

    public PieceColor SideToMove { get; }

    public CastlingRights Castling { get; }

    /// <summary>
    /// Where the king and castling rooks started. With a non-standard (Chess960) layout a castling move is stored
    /// as the king moving onto its own rook ("b1h1"), since the king may travel 0, 1 or several squares.
    /// </summary>
    public CastlingLayout Layout { get; }

    /// <summary>The square a pawn may capture onto en passant, or -1.</summary>
    public int EnPassantSquare { get; }

    public int HalfmoveClock { get; }

    public int FullmoveNumber { get; }

    public Piece this[int square] => _board[square];

    public static Position FromFen(string fen)
    {
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            throw new FormatException("FEN must have at least 4 fields.");
        }

        var board = new Piece[64];
        var ranks = parts[0].Split('/');
        if (ranks.Length != 8)
        {
            throw new FormatException("FEN board must have 8 ranks.");
        }

        for (var i = 0; i < 8; i++)
        {
            var rank = 7 - i;
            var file = 0;
            foreach (var c in ranks[i])
            {
                if (char.IsDigit(c))
                {
                    file += c - '0';
                }
                else
                {
                    if (file > 7)
                    {
                        throw new FormatException($"Too many squares in FEN rank {rank + 1}.");
                    }

                    board[Square.At(file, rank)] = Piece.FromFenChar(c);
                    file++;
                }
            }

            if (file != 8)
            {
                throw new FormatException($"FEN rank {rank + 1} does not have 8 squares.");
            }
        }

        var side = parts[1] switch
        {
            "w" => PieceColor.White,
            "b" => PieceColor.Black,
            _ => throw new FormatException("FEN side to move must be 'w' or 'b'."),
        };

        var (castling, layout) = ParseCastling(parts[2], board);
        var enPassant = parts[3] == "-" ? -1 : Square.Parse(parts[3]);
        var halfmove = parts.Length > 4 ? int.Parse(parts[4]) : 0;
        var fullmove = parts.Length > 5 ? int.Parse(parts[5]) : 1;

        var position = new Position(board, side, castling, enPassant, halfmove, fullmove, layout);
        if (position.FindKing(PieceColor.White) < 0 || position.FindKing(PieceColor.Black) < 0)
        {
            throw new FormatException("Both sides must have a king.");
        }

        return position;
    }

    public string ToFen()
    {
        var sb = new StringBuilder();
        sb.Append(BoardFen());
        sb.Append(SideToMove == PieceColor.White ? " w " : " b ");
        sb.Append(CastlingFen());
        sb.Append(' ');
        sb.Append(EnPassantSquare < 0 ? "-" : Square.Name(EnPassantSquare));
        sb.Append(' ').Append(HalfmoveClock).Append(' ').Append(FullmoveNumber);
        return sb.ToString();
    }

    /// <summary>
    /// Identifies a position for repetition purposes: placement, side to move, castling rights and
    /// en passant square (only when an en passant capture is actually possible).
    /// </summary>
    public string RepetitionKey()
    {
        var ep = EnPassantSquare >= 0 && GenerateLegalMoves().Any(m => m.To == EnPassantSquare && _board[m.From].Type == PieceType.Pawn)
            ? Square.Name(EnPassantSquare)
            : "-";
        return $"{BoardFen()} {(SideToMove == PieceColor.White ? 'w' : 'b')} {CastlingFen()} {ep}";
    }

    public int FindKing(PieceColor color)
    {
        for (var sq = 0; sq < 64; sq++)
        {
            if (_board[sq] == new Piece(PieceType.King, color))
            {
                return sq;
            }
        }

        return -1;
    }

    public bool IsInCheck() => IsInCheck(SideToMove);

    public bool IsInCheck(PieceColor color) => IsSquareAttacked(FindKing(color), color.Opponent());

    public bool IsSquareAttacked(int square, PieceColor byColor)
    {
        var file = Square.File(square);
        var rank = Square.Rank(square);

        // Pawns attack diagonally forward, so look one rank "behind" the target from the attacker's view.
        var pawnRank = byColor == PieceColor.White ? rank - 1 : rank + 1;
        foreach (var df in (int[])[-1, 1])
        {
            if (Square.IsOnBoard(file + df, pawnRank) &&
                _board[Square.At(file + df, pawnRank)] == new Piece(PieceType.Pawn, byColor))
            {
                return true;
            }
        }

        if (AttackedByStep(file, rank, KnightOffsets, new Piece(PieceType.Knight, byColor)) ||
            AttackedByStep(file, rank, KingOffsets, new Piece(PieceType.King, byColor)))
        {
            return true;
        }

        return AttackedBySlider(file, rank, RookDirections, byColor, PieceType.Rook) ||
               AttackedBySlider(file, rank, BishopDirections, byColor, PieceType.Bishop);
    }

    public IReadOnlyList<Move> GenerateLegalMoves()
    {
        var legal = new List<Move>();
        foreach (var move in GeneratePseudoLegalMoves())
        {
            if (!Apply(move).IsInCheck(SideToMove))
            {
                legal.Add(move);
            }
        }

        return legal;
    }

    public bool IsLegal(Move move) => GenerateLegalMoves().Contains(move);

    /// <summary>True if the side to move can force checkmate within <paramref name="moves"/> of its own moves.</summary>
    public bool CanForceMate(int moves) =>
        moves > 0 && GenerateLegalMoves().Any(m => Apply(m).IsMatedWithin(moves - 1));

    /// <summary>
    /// True if the side to move is checkmated now, or the opponent can force mate within <paramref name="moves"/>
    /// of its own moves whatever the side to move plays.
    /// </summary>
    public bool IsMatedWithin(int moves)
    {
        var replies = GenerateLegalMoves();
        return replies.Count == 0 ? IsInCheck() : replies.All(r => Apply(r).CanForceMate(moves));
    }

    /// <summary>
    /// True for a castling move: the king moving two files in normal chess, or the king moving onto its own rook
    /// with a Chess960 layout. <paramref name="kingSide"/> says which way.
    /// </summary>
    public bool IsCastling(Move move, out bool kingSide)
    {
        var piece = _board[move.From];
        kingSide = Square.File(move.To) > Square.File(move.From);
        if (piece.Type != PieceType.King)
        {
            return false;
        }

        return Layout.IsStandard
            ? Math.Abs(Square.File(move.To) - Square.File(move.From)) == 2 && Square.Rank(move.To) == Square.Rank(move.From)
            : _board[move.To] == new Piece(PieceType.Rook, piece.Color);
    }

    /// <summary>Plays a move without checking legality. Use <see cref="IsLegal"/> first for untrusted input.</summary>
    public Position Apply(Move move)
    {
        var board = (Piece[])_board.Clone();
        var piece = board[move.From];
        var isCastling = IsCastling(move, out var kingSide);
        var captured = isCastling ? Piece.Empty : board[move.To];
        var isPawn = piece.Type == PieceType.Pawn;
        var fromFile = Square.File(move.From);
        var toFile = Square.File(move.To);

        if (isCastling)
        {
            var rank = Square.Rank(move.From);
            var rookFrom = Square.At(kingSide ? Layout.KingRookFile : Layout.QueenRookFile, rank);
            var rook = board[rookFrom];

            // Lift both pieces first: in Chess960 the king or rook can land on the other's starting square.
            board[move.From] = Piece.Empty;
            board[rookFrom] = Piece.Empty;
            board[Square.At(kingSide ? 6 : 2, rank)] = piece;
            board[Square.At(kingSide ? 5 : 3, rank)] = rook;
        }
        else
        {
            board[move.To] = move.Promotion == PieceType.None ? piece : new Piece(move.Promotion, piece.Color);
            board[move.From] = Piece.Empty;
        }

        var isEnPassant = isPawn && move.To == EnPassantSquare && fromFile != toFile && captured.IsEmpty;
        if (isEnPassant)
        {
            board[Square.At(toFile, Square.Rank(move.From))] = Piece.Empty;
        }

        var castling = Castling & ~(CastlingLostBy(move.From) | CastlingLostBy(move.To));
        if (piece.Type == PieceType.King)
        {
            castling &= piece.Color == PieceColor.White
                ? ~(CastlingRights.WhiteKingSide | CastlingRights.WhiteQueenSide)
                : ~(CastlingRights.BlackKingSide | CastlingRights.BlackQueenSide);
        }

        var enPassant = -1;
        if (isPawn && Math.Abs(Square.Rank(move.To) - Square.Rank(move.From)) == 2)
        {
            enPassant = (move.From + move.To) / 2;
        }

        var resetsClock = isPawn || !captured.IsEmpty;
        return new Position(
            board,
            SideToMove.Opponent(),
            castling,
            enPassant,
            resetsClock ? 0 : HalfmoveClock + 1,
            SideToMove == PieceColor.Black ? FullmoveNumber + 1 : FullmoveNumber,
            Layout);
    }

    /// <summary>
    /// Changes pieces on the board in place of a move: the turn passes, en passant is cleared and castling rights
    /// are dropped for any king or rook no longer on its home square. Does not check legality.
    /// With <paramref name="passTurn"/> false (passive dice rules firing between turns) the same side stays to move.
    /// </summary>
    public Position WithEdits(IEnumerable<SquareEdit> edits, bool passTurn = true)
    {
        var board = (Piece[])_board.Clone();
        foreach (var edit in edits)
        {
            board[edit.Square] = edit.Piece;
        }

        var castling = Castling;
        foreach (var (right, rookFile, color) in new[]
                 {
                     (CastlingRights.WhiteKingSide, Layout.KingRookFile, PieceColor.White),
                     (CastlingRights.WhiteQueenSide, Layout.QueenRookFile, PieceColor.White),
                     (CastlingRights.BlackKingSide, Layout.KingRookFile, PieceColor.Black),
                     (CastlingRights.BlackQueenSide, Layout.QueenRookFile, PieceColor.Black),
                 })
        {
            var rank = color == PieceColor.White ? 0 : 7;
            if (board[Square.At(Layout.KingFile, rank)] != new Piece(PieceType.King, color) ||
                board[Square.At(rookFile, rank)] != new Piece(PieceType.Rook, color))
            {
                castling &= ~right;
            }
        }

        return new Position(
            board,
            passTurn ? SideToMove.Opponent() : SideToMove,
            castling,
            -1,
            0,
            passTurn && SideToMove == PieceColor.Black ? FullmoveNumber + 1 : FullmoveNumber,
            Layout);
    }

    /// <summary>True when neither side can possibly deliver mate (K v K, K+minor v K, K+B v K+B on same colour).</summary>
    public bool IsInsufficientMaterial()
    {
        var minors = new List<(Piece Piece, int Square)>();
        for (var sq = 0; sq < 64; sq++)
        {
            var p = _board[sq];
            switch (p.Type)
            {
                case PieceType.None or PieceType.King:
                    continue;
                case PieceType.Knight or PieceType.Bishop:
                    minors.Add((p, sq));
                    break;
                default:
                    return false;
            }
        }

        if (minors.Count <= 1)
        {
            return true;
        }

        // Any number of bishops, all on the same square colour, cannot mate.
        return minors.All(m => m.Piece.Type == PieceType.Bishop) &&
               minors.Select(m => Square.IsLight(m.Square)).Distinct().Count() == 1;
    }

    /// <summary>Rights lost when a piece leaves, or is captured on, a rook's starting square. King moves are handled in Apply.</summary>
    private CastlingRights CastlingLostBy(int square)
    {
        var lost = CastlingRights.None;
        if (square == Square.At(Layout.KingRookFile, 0)) lost |= CastlingRights.WhiteKingSide;
        if (square == Square.At(Layout.QueenRookFile, 0)) lost |= CastlingRights.WhiteQueenSide;
        if (square == Square.At(Layout.KingRookFile, 7)) lost |= CastlingRights.BlackKingSide;
        if (square == Square.At(Layout.QueenRookFile, 7)) lost |= CastlingRights.BlackQueenSide;
        return lost;
    }

    /// <summary>
    /// Reads the FEN castling field: "KQkq" (in Chess960, the outermost rook on that side, as in X-FEN) or the
    /// rooks' file letters as in Shredder-FEN ("HAha", "GBgb"). The king's file is wherever a king with rights stands.
    /// </summary>
    private static (CastlingRights Rights, CastlingLayout Layout) ParseCastling(string field, Piece[] board)
    {
        var rights = CastlingRights.None;
        var (kingFile, kingRook, queenRook) = (CastlingLayout.Standard.KingFile, (int?)null, (int?)null);
        if (field == "-")
        {
            return (rights, CastlingLayout.Standard);
        }

        foreach (var c in field)
        {
            var color = char.IsUpper(c) ? PieceColor.White : PieceColor.Black;
            var rank = color == PieceColor.White ? 0 : 7;
            var king = Enumerable.Range(0, 8).FirstOrDefault(f => board[Square.At(f, rank)] == new Piece(PieceType.King, color), -1);
            if (king < 0)
            {
                throw new FormatException($"Castling right '{c}' but no king on the back rank.");
            }

            var rook = new Piece(PieceType.Rook, color);
            int rookFile;
            switch (char.ToLowerInvariant(c))
            {
                case 'k':
                    rookFile = Enumerable.Range(king + 1, 7 - king).Reverse().FirstOrDefault(f => board[Square.At(f, rank)] == rook, -1);
                    break;
                case 'q':
                    rookFile = Enumerable.Range(0, king).FirstOrDefault(f => board[Square.At(f, rank)] == rook, -1);
                    break;
                case >= 'a' and <= 'h':
                    rookFile = char.ToLowerInvariant(c) - 'a';
                    break;
                default:
                    throw new FormatException($"Invalid castling character '{c}'.");
            }

            if (rookFile < 0 || rookFile == king)
            {
                // "KQkq" on a board where that rook is gone: the right can't be used, so drop it.
                continue;
            }

            var kingSide = rookFile > king;
            kingFile = king;
            if (kingSide) kingRook = rookFile; else queenRook = rookFile;
            rights |= (color, kingSide) switch
            {
                (PieceColor.White, true) => CastlingRights.WhiteKingSide,
                (PieceColor.White, false) => CastlingRights.WhiteQueenSide,
                (PieceColor.Black, true) => CastlingRights.BlackKingSide,
                _ => CastlingRights.BlackQueenSide,
            };
        }

        var layout = new CastlingLayout(
            kingFile,
            kingRook ?? CastlingLayout.Standard.KingRookFile,
            queenRook ?? CastlingLayout.Standard.QueenRookFile);
        return (rights, layout);
    }

    private string BoardFen()
    {
        var sb = new StringBuilder();
        for (var rank = 7; rank >= 0; rank--)
        {
            var empty = 0;
            for (var file = 0; file < 8; file++)
            {
                var p = _board[Square.At(file, rank)];
                if (p.IsEmpty)
                {
                    empty++;
                    continue;
                }

                if (empty > 0)
                {
                    sb.Append(empty);
                    empty = 0;
                }

                sb.Append(p.ToFenChar());
            }

            if (empty > 0)
            {
                sb.Append(empty);
            }

            if (rank > 0)
            {
                sb.Append('/');
            }
        }

        return sb.ToString();
    }

    private string CastlingFen()
    {
        if (Castling == CastlingRights.None)
        {
            return "-";
        }

        // Chess960 layouts use the rooks' file letters (Shredder-FEN), which lichess and most engines understand.
        var sb = new StringBuilder();
        var standard = Layout.IsStandard;
        var kingRook = (char)('a' + Layout.KingRookFile);
        var queenRook = (char)('a' + Layout.QueenRookFile);
        if (Castling.HasFlag(CastlingRights.WhiteKingSide)) sb.Append(standard ? 'K' : char.ToUpperInvariant(kingRook));
        if (Castling.HasFlag(CastlingRights.WhiteQueenSide)) sb.Append(standard ? 'Q' : char.ToUpperInvariant(queenRook));
        if (Castling.HasFlag(CastlingRights.BlackKingSide)) sb.Append(standard ? 'k' : kingRook);
        if (Castling.HasFlag(CastlingRights.BlackQueenSide)) sb.Append(standard ? 'q' : queenRook);
        return sb.ToString();
    }

    private bool AttackedByStep(int file, int rank, (int File, int Rank)[] offsets, Piece attacker)
    {
        foreach (var (df, dr) in offsets)
        {
            if (Square.IsOnBoard(file + df, rank + dr) && _board[Square.At(file + df, rank + dr)] == attacker)
            {
                return true;
            }
        }

        return false;
    }

    private bool AttackedBySlider(int file, int rank, (int File, int Rank)[] directions, PieceColor byColor, PieceType slider)
    {
        foreach (var (df, dr) in directions)
        {
            var f = file + df;
            var r = rank + dr;
            while (Square.IsOnBoard(f, r))
            {
                var p = _board[Square.At(f, r)];
                if (!p.IsEmpty)
                {
                    if (p.Color == byColor && (p.Type == slider || p.Type == PieceType.Queen))
                    {
                        return true;
                    }

                    break;
                }

                f += df;
                r += dr;
            }
        }

        return false;
    }

    private IEnumerable<Move> GeneratePseudoLegalMoves()
    {
        var moves = new List<Move>();
        for (var sq = 0; sq < 64; sq++)
        {
            var p = _board[sq];
            if (p.IsEmpty || p.Color != SideToMove)
            {
                continue;
            }

            switch (p.Type)
            {
                case PieceType.Pawn:
                    AddPawnMoves(sq, moves);
                    break;
                case PieceType.Knight:
                    AddStepMoves(sq, KnightOffsets, moves);
                    break;
                case PieceType.Bishop:
                    AddSlideMoves(sq, BishopDirections, moves);
                    break;
                case PieceType.Rook:
                    AddSlideMoves(sq, RookDirections, moves);
                    break;
                case PieceType.Queen:
                    AddSlideMoves(sq, RookDirections, moves);
                    AddSlideMoves(sq, BishopDirections, moves);
                    break;
                case PieceType.King:
                    AddStepMoves(sq, KingOffsets, moves);
                    AddCastlingMoves(sq, moves);
                    break;
            }
        }

        return moves;
    }

    private void AddPawnMoves(int from, List<Move> moves)
    {
        var file = Square.File(from);
        var rank = Square.Rank(from);
        var dir = SideToMove == PieceColor.White ? 1 : -1;
        var startRank = SideToMove == PieceColor.White ? 1 : 6;
        var promotionRank = SideToMove == PieceColor.White ? 7 : 0;

        void AddPawnMove(int to)
        {
            if (Square.Rank(to) == promotionRank)
            {
                foreach (var promo in PromotionPieces)
                {
                    moves.Add(new Move(from, to, promo));
                }
            }
            else
            {
                moves.Add(new Move(from, to));
            }
        }

        var oneAhead = Square.At(file, rank + dir);
        if (_board[oneAhead].IsEmpty)
        {
            AddPawnMove(oneAhead);
            if (rank == startRank && _board[Square.At(file, rank + 2 * dir)].IsEmpty)
            {
                moves.Add(new Move(from, Square.At(file, rank + 2 * dir)));
            }
        }

        foreach (var df in (int[])[-1, 1])
        {
            if (!Square.IsOnBoard(file + df, rank + dir))
            {
                continue;
            }

            var to = Square.At(file + df, rank + dir);
            var target = _board[to];
            if ((!target.IsEmpty && target.Color != SideToMove) || to == EnPassantSquare)
            {
                AddPawnMove(to);
            }
        }
    }

    private void AddStepMoves(int from, (int File, int Rank)[] offsets, List<Move> moves)
    {
        var file = Square.File(from);
        var rank = Square.Rank(from);
        foreach (var (df, dr) in offsets)
        {
            if (!Square.IsOnBoard(file + df, rank + dr))
            {
                continue;
            }

            var to = Square.At(file + df, rank + dr);
            var target = _board[to];
            if (target.IsEmpty || target.Color != SideToMove)
            {
                moves.Add(new Move(from, to));
            }
        }
    }

    private void AddSlideMoves(int from, (int File, int Rank)[] directions, List<Move> moves)
    {
        foreach (var (df, dr) in directions)
        {
            var f = Square.File(from) + df;
            var r = Square.Rank(from) + dr;
            while (Square.IsOnBoard(f, r))
            {
                var to = Square.At(f, r);
                var target = _board[to];
                if (target.IsEmpty)
                {
                    moves.Add(new Move(from, to));
                }
                else
                {
                    if (target.Color != SideToMove)
                    {
                        moves.Add(new Move(from, to));
                    }

                    break;
                }

                f += df;
                r += dr;
            }
        }
    }

    private void AddCastlingMoves(int kingSquare, List<Move> moves)
    {
        var white = SideToMove == PieceColor.White;
        var homeRank = white ? 0 : 7;
        if (kingSquare != Square.At(Layout.KingFile, homeRank))
        {
            return;
        }

        var kingSide = white ? CastlingRights.WhiteKingSide : CastlingRights.BlackKingSide;
        var queenSide = white ? CastlingRights.WhiteQueenSide : CastlingRights.BlackQueenSide;
        if (Castling.HasFlag(kingSide))
        {
            AddCastle(kingSquare, homeRank, Layout.KingRookFile, kingTo: 6, rookTo: 5, moves);
        }

        if (Castling.HasFlag(queenSide))
        {
            AddCastle(kingSquare, homeRank, Layout.QueenRookFile, kingTo: 2, rookTo: 3, moves);
        }
    }

    /// <summary>
    /// Every square the king or rook passes over or lands on must be empty (apart from those two pieces), and the
    /// king can't start in, pass through or land on an attacked square. This covers normal chess and Chess960 alike.
    /// </summary>
    private void AddCastle(int kingSquare, int rank, int rookFile, int kingTo, int rookTo, List<Move> moves)
    {
        var rookSquare = Square.At(rookFile, rank);
        if (_board[rookSquare] != new Piece(PieceType.Rook, SideToMove))
        {
            return;
        }

        var kingFile = Square.File(kingSquare);
        var low = Math.Min(Math.Min(kingFile, kingTo), Math.Min(rookFile, rookTo));
        var high = Math.Max(Math.Max(kingFile, kingTo), Math.Max(rookFile, rookTo));
        for (var f = low; f <= high; f++)
        {
            var sq = Square.At(f, rank);
            if (sq != kingSquare && sq != rookSquare && !_board[sq].IsEmpty)
            {
                return;
            }
        }

        var opponent = SideToMove.Opponent();
        for (var f = Math.Min(kingFile, kingTo); f <= Math.Max(kingFile, kingTo); f++)
        {
            if (IsSquareAttacked(Square.At(f, rank), opponent))
            {
                return;
            }
        }

        moves.Add(new Move(kingSquare, Layout.IsStandard ? Square.At(kingTo, rank) : rookSquare));
    }
}

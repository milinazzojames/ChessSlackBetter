using System.Text;
using ChessSlackBetter.Chess;
using SkiaSharp;
using Svg.Skia;

namespace ChessSlackBetter.Bot.Rendering;

/// <summary>Draws a position as a PNG, lichess-style: themed squares and pieces, last move and check highlighted.</summary>
public sealed class BoardRenderer : IDisposable
{
    public const int SquareSize = 64;
    public const int BoardSize = SquareSize * 8;

    private readonly Dictionary<(PieceSet, Piece), SKSvg> _pieces = new();

    public BoardRenderer()
    {
        var assembly = typeof(BoardRenderer).Assembly;
        foreach (var set in PieceSet.All)
        {
            foreach (var color in new[] { PieceColor.White, PieceColor.Black })
            {
                foreach (var type in new[] { PieceType.King, PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight, PieceType.Pawn })
                {
                    var name = $"{(color == PieceColor.White ? 'w' : 'b')}{(type == PieceType.Pawn ? "P" : type.SanLetter())}.svg";
                    using var stream = assembly.GetManifestResourceStream($"ChessSlackBetter.Bot.Rendering.Pieces.{set.Name}.{name}")
                                       ?? throw new InvalidOperationException($"Missing embedded piece image {set.Name}/{name}.");
                    var svg = new SKSvg();
                    svg.Load(stream);
                    _pieces[(set, new Piece(type, color))] = svg;
                }
            }
        }
    }

    /// <param name="position">The position to draw.</param>
    /// <param name="highlights">Squares to highlight: the last move's from/to, or the squares a dice roll changed.</param>
    /// <param name="perspective">The side shown at the bottom of the board.</param>
    /// <param name="theme">Square colours; <see cref="BoardTheme.Default"/> when null.</param>
    /// <param name="pieces">Piece images; <see cref="PieceSet.Default"/> when null.</param>
    public byte[] RenderPng(
        Position position, IReadOnlyCollection<int> highlights, PieceColor perspective, BoardTheme? theme = null, PieceSet? pieces = null)
    {
        theme ??= BoardTheme.Default;
        pieces ??= PieceSet.Default;
        using var surface = SKSurface.Create(new SKImageInfo(BoardSize, BoardSize));
        var canvas = surface.Canvas;
        using var fill = new SKPaint { IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 13) { Embolden = true };

        var checkedKing = position.IsInCheck() ? position.FindKing(position.SideToMove) : -1;

        for (var sq = 0; sq < 64; sq++)
        {
            var (x, y) = Origin(sq, perspective);
            var rect = SKRect.Create(x, y, SquareSize, SquareSize);
            var light = Square.IsLight(sq);
            var highlighted = highlights.Contains(sq);
            fill.Shader = null;
            fill.Color = highlighted ? (light ? theme.LastMoveLight : theme.LastMoveDark) : (light ? theme.Light : theme.Dark);
            canvas.DrawRect(rect, fill);

            if (sq == checkedKing)
            {
                using var glow = SKShader.CreateRadialGradient(
                    new SKPoint(rect.MidX, rect.MidY),
                    SquareSize * 0.6f,
                    [new SKColor(255, 0, 0), new SKColor(231, 0, 0), new SKColor(169, 0, 0, 0)],
                    [0f, 0.25f, 1f],
                    SKShaderTileMode.Clamp);
                fill.Shader = glow;
                canvas.DrawRect(rect, fill);
                fill.Shader = null;
            }

            DrawCoordinates(canvas, font, sq, x, y, perspective, light ? theme.Dark : theme.Light);

            var piece = position[sq];
            if (!piece.IsEmpty && _pieces[(pieces, piece)].Picture is { } picture)
            {
                var scale = SquareSize / picture.CullRect.Width;
                var matrix = SKMatrix.CreateScale(scale, scale).PostConcat(SKMatrix.CreateTranslation(x, y));
                canvas.DrawPicture(picture, in matrix);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Monospace board for when no public URL is configured for images.</summary>
    public static string RenderText(Position position, PieceColor perspective)
    {
        var sb = new StringBuilder();
        var ranks = perspective == PieceColor.White ? Enumerable.Range(0, 8).Reverse() : Enumerable.Range(0, 8);
        var files = perspective == PieceColor.White ? Enumerable.Range(0, 8) : Enumerable.Range(0, 8).Reverse();
        foreach (var rank in ranks)
        {
            sb.Append(rank + 1).Append(' ');
            foreach (var file in files)
            {
                var sq = Square.At(file, rank);
                var piece = position[sq];
                sb.Append(piece.IsEmpty ? (Square.IsLight(sq) ? '·' : ':') : piece.ToFenChar()).Append(' ');
            }

            sb.AppendLine();
        }

        sb.Append("  ").Append(string.Join(' ', files.Select(f => (char)('a' + f))));
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var svg in _pieces.Values)
        {
            svg.Dispose();
        }
    }

    private static (int X, int Y) Origin(int square, PieceColor perspective)
    {
        var file = Square.File(square);
        var rank = Square.Rank(square);
        return perspective == PieceColor.White
            ? (file * SquareSize, (7 - rank) * SquareSize)
            : ((7 - file) * SquareSize, rank * SquareSize);
    }

    private static void DrawCoordinates(SKCanvas canvas, SKFont font, int sq, int x, int y, PieceColor perspective, SKColor color)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        var bottomRank = perspective == PieceColor.White ? 0 : 7;
        var leftFile = perspective == PieceColor.White ? 0 : 7;

        if (Square.Rank(sq) == bottomRank)
        {
            var label = ((char)('a' + Square.File(sq))).ToString();
            canvas.DrawText(label, x + SquareSize - 4, y + SquareSize - 4, SKTextAlign.Right, font, paint);
        }

        if (Square.File(sq) == leftFile)
        {
            var label = ((char)('1' + Square.Rank(sq))).ToString();
            canvas.DrawText(label, x + 3, y + 14, SKTextAlign.Left, font, paint);
        }
    }
}

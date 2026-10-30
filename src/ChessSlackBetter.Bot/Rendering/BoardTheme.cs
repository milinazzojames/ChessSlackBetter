using SkiaSharp;

namespace ChessSlackBetter.Bot.Rendering;

/// <summary>Square colours for the board image. Players pick one with the <c>theme</c> command.</summary>
/// <param name="Highlight">
/// Colour laid over the last move's squares; null for lichess's yellow-green, which turns muddy on some colours.
/// </param>
public sealed record BoardTheme(string Name, SKColor Light, SKColor Dark, SKColor? Highlight = null)
{
    public static readonly BoardTheme Brown = new("brown", SKColor.Parse("#f0d9b5"), SKColor.Parse("#b58863"));

    public static readonly IReadOnlyList<BoardTheme> All =
    [
        Brown,
        new("blue", SKColor.Parse("#dee3e6"), SKColor.Parse("#8ca2ad")),
        new("green", SKColor.Parse("#ffffdd"), SKColor.Parse("#86a666")),
        new("purple", SKColor.Parse("#9f90b0"), SKColor.Parse("#7d4a8d"), SKColor.Parse("#64b4ff")),
        new("grey", SKColor.Parse("#d9d9d9"), SKColor.Parse("#a3a3a3")),
        new("pink", SKColor.Parse("#f9e1e8"), SKColor.Parse("#d98ba3"), SKColor.Parse("#e63c82")),
        // Bolt Depot green (#005854), lifted a shade so black pieces stand out on the dark squares.
        new("bolt", SKColor.Parse("#d7e6e4"), SKColor.Parse("#2b7a75"), SKColor.Parse("#64b4ff")),
    ];

    public static BoardTheme Default => Brown;

    // The last-move highlight is translucent, as on lichess.
    private static readonly SKColor DefaultHighlight = new(155, 199, 0);
    private const float HighlightAlpha = 0.41f;

    public SKColor LastMoveLight => Overlay(Light);

    public SKColor LastMoveDark => Overlay(Dark);

    public static BoardTheme? Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private SKColor Overlay(SKColor square)
    {
        var over = Highlight ?? DefaultHighlight;
        static byte Mix(byte under, byte over) => (byte)Math.Round(under * (1 - HighlightAlpha) + over * HighlightAlpha);
        return new SKColor(Mix(square.Red, over.Red), Mix(square.Green, over.Green), Mix(square.Blue, over.Blue));
    }
}

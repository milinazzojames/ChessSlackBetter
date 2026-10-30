namespace ChessSlackBetter.Bot.Rendering;

/// <summary>
/// A set of piece images, embedded from <c>Rendering/Pieces/{Name}/</c> with lichess's file names (wK.svg, bP.svg, ...).
/// Players pick one with the <c>pieces</c> command. Sources and licences are credited in the README.
/// </summary>
public sealed record PieceSet(string Name)
{
    public static readonly PieceSet Cburnett = new("cburnett");

    public static readonly IReadOnlyList<PieceSet> All =
    [
        Cburnett,
        new("chessnut"),
        new("fantasy"),
        new("rhosgfx"),
        new("bolt"),
    ];

    public static PieceSet Default => Cburnett;

    public static PieceSet? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

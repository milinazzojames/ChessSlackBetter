namespace ChessSlackBetter.Bot.Games;

/// <summary>
/// Elo ratings for two-player games. Everyone starts at <see cref="StartingRating"/>, and each finished game moves
/// both players by <see cref="KFactor"/> × (score − expected score), so beating a stronger player gains more than
/// beating a weaker one. Ratings aren't stored: they're replayed from the finished games, oldest first.
/// </summary>
public sealed class Elo
{
    public const double StartingRating = 1200;

    public const double KFactor = 32;

    private readonly Dictionary<string, double> _ratings = [];

    public double RatingOf(string userId) => _ratings.GetValueOrDefault(userId, StartingRating);

    /// <summary>The chance (0–1) that a player rated <paramref name="rating"/> beats one rated <paramref name="opponentRating"/>, counting a draw as half.</summary>
    public static double ExpectedScore(double rating, double opponentRating) =>
        1 / (1 + Math.Pow(10, (opponentRating - rating) / 400));

    /// <summary>Applies one game's result and returns how much each player's rating changed.</summary>
    /// <param name="whiteScore">1 if white won, 0 if black won, 0.5 for a draw.</param>
    public (double White, double Black) Record(string whiteId, string blackId, double whiteScore)
    {
        var white = RatingOf(whiteId);
        var black = RatingOf(blackId);
        var change = KFactor * (whiteScore - ExpectedScore(white, black));
        _ratings[whiteId] = white + change;
        _ratings[blackId] = black - change;
        return (change, -change);
    }
}

using ChessSlackBetter.Bot.Games;

namespace ChessSlackBetter.Bot.Tests;

public class EloTests
{
    [Fact]
    public void New_players_start_at_1200_and_equal_players_move_by_half_k()
    {
        var elo = new Elo();
        Assert.Equal(1200, elo.RatingOf("A"));

        var (white, black) = elo.Record("A", "B", 1);
        Assert.Equal(16, white, 6);
        Assert.Equal(-16, black, 6);
        Assert.Equal(1216, elo.RatingOf("A"), 6);
        Assert.Equal(1184, elo.RatingOf("B"), 6);
    }

    [Fact]
    public void Upsets_move_ratings_more_than_expected_results()
    {
        var elo = new Elo();
        for (var i = 0; i < 5; i++)
        {
            elo.Record("Strong", "Weak", 1);
        }

        var expected = new Elo();
        for (var i = 0; i < 5; i++)
        {
            expected.Record("Strong", "Weak", 1);
        }

        var (_, favouriteWins) = expected.Record("Strong", "Weak", 1);
        var (_, upset) = elo.Record("Strong", "Weak", 0);
        Assert.True(upset > 16);
        Assert.True(-favouriteWins < 16);
    }

    [Fact]
    public void A_draw_between_equals_changes_nothing_and_ratings_are_zero_sum()
    {
        var elo = new Elo();
        Assert.Equal((0, 0), elo.Record("A", "B", 0.5));

        elo.Record("A", "C", 1);
        elo.Record("C", "B", 0.5);
        Assert.Equal(3 * 1200, elo.RatingOf("A") + elo.RatingOf("B") + elo.RatingOf("C"), 6);
    }

    [Theory]
    [InlineData(1200, 1200, 0.5)]
    [InlineData(1600, 1200, 0.909)]
    [InlineData(1200, 1600, 0.091)]
    public void Expected_score(double rating, double opponent, double expected) =>
        Assert.Equal(expected, Elo.ExpectedScore(rating, opponent), 3);
}

using System.Text.Json;
using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Chess;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Games;

public interface IGameRepository
{
    Task<GameRecord?> GetAsync(string channelId, string threadTs, CancellationToken ct = default);

    Task SaveAsync(GameRecord game, CancellationToken ct = default);

    /// <summary>Games the user is in that are waiting to be accepted or still being played.</summary>
    Task<IReadOnlyList<GameRecord>> GetOpenGamesAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<GameRecord>> GetFinishedGamesAsync(CancellationToken ct = default);
}

public sealed class SqliteGameRepository : IGameRepository
{
    private const string Columns =
        "channel_id, thread_ts, challenger_id, opponent_id, white_id, black_id, state, moves, draw_offered_by, outcome, termination, analysis_url, created_at, updated_at, mode, dice";

    private readonly string _connectionString;

    public SqliteGameRepository(IOptions<ChessBotOptions> options)
        : this(options.Value.DatabasePath)
    {
    }

    public SqliteGameRepository(string databasePath)
    {
        _connectionString = Database.ConnectionString(databasePath);
        Database.EnsureSchema(_connectionString);
    }

    public async Task<GameRecord?> GetAsync(string channelId, string threadTs, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM games WHERE channel_id = $channel AND thread_ts = $thread";
        command.Parameters.AddWithValue("$channel", channelId);
        command.Parameters.AddWithValue("$thread", threadTs);
        var results = await ReadAllAsync(command, ct);
        return results.FirstOrDefault();
    }

    public async Task SaveAsync(GameRecord game, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO games ({Columns})
            VALUES ($channel, $thread, $challenger, $opponent, $white, $black, $state, $moves, $draw, $outcome, $termination, $analysis, $created, $updated, $mode, $dice)
            ON CONFLICT (channel_id, thread_ts) DO UPDATE SET
                state = excluded.state,
                dice = excluded.dice,
                moves = excluded.moves,
                draw_offered_by = excluded.draw_offered_by,
                outcome = excluded.outcome,
                termination = excluded.termination,
                analysis_url = excluded.analysis_url,
                updated_at = excluded.updated_at;
            INSERT OR IGNORE INTO users (user_id, created_at) VALUES ($white, $created), ($black, $created);
            """;
        command.Parameters.AddWithValue("$channel", game.ChannelId);
        command.Parameters.AddWithValue("$thread", game.ThreadTs);
        command.Parameters.AddWithValue("$challenger", game.ChallengerId);
        command.Parameters.AddWithValue("$opponent", game.OpponentId);
        command.Parameters.AddWithValue("$white", game.WhiteId);
        command.Parameters.AddWithValue("$black", game.BlackId);
        command.Parameters.AddWithValue("$state", game.State.ToString());
        command.Parameters.AddWithValue("$moves", string.Join(' ', game.Moves));
        command.Parameters.AddWithValue("$draw", (object?)game.DrawOfferedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", game.Outcome.ToString());
        command.Parameters.AddWithValue("$termination", game.Termination.ToString());
        command.Parameters.AddWithValue("$analysis", (object?)game.AnalysisUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", game.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", game.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$mode", game.Mode.ToString());
        command.Parameters.AddWithValue("$dice", game.Mode == GameMode.DiceRoll ? JsonSerializer.Serialize(game.Dice) : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<GameRecord>> GetOpenGamesAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM games
            WHERE (white_id = $user OR black_id = $user) AND state IN ('Challenged', 'Active')
            ORDER BY updated_at DESC
            """;
        command.Parameters.AddWithValue("$user", userId);
        return await ReadAllAsync(command, ct);
    }

    public async Task<IReadOnlyList<GameRecord>> GetFinishedGamesAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM games WHERE state = 'Finished'";
        return await ReadAllAsync(command, ct);
    }

    private SqliteConnection Open() => Database.Open(_connectionString);

    private static async Task<List<GameRecord>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var results = new List<GameRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new GameRecord
            {
                ChannelId = reader.GetString(0),
                ThreadTs = reader.GetString(1),
                ChallengerId = reader.GetString(2),
                OpponentId = reader.GetString(3),
                WhiteId = reader.GetString(4),
                BlackId = reader.GetString(5),
                State = Enum.Parse<GameState>(reader.GetString(6)),
                Moves = reader.GetString(7).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                DrawOfferedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
                Outcome = Enum.Parse<GameOutcome>(reader.GetString(9)),
                Termination = Enum.Parse<Termination>(reader.GetString(10)),
                AnalysisUrl = reader.IsDBNull(11) ? null : reader.GetString(11),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(12)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(13)),
                Mode = Enum.Parse<GameMode>(reader.GetString(14)),
                Dice = reader.IsDBNull(15) ? new DiceState() : JsonSerializer.Deserialize<DiceState>(reader.GetString(15)) ?? new DiceState(),
            });
        }

        return results;
    }
}

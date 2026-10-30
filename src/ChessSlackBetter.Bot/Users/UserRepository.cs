using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Users;

/// <summary>How a user wants their board drawn. Each is a name, or null if they haven't picked one.</summary>
public sealed record UserPreferences(string? Theme, string? Pieces)
{
    public static readonly UserPreferences None = new(null, null);
}

/// <summary>Per-user preferences. Users are keyed by Slack user id.</summary>
public interface IUserRepository
{
    Task<UserPreferences> GetPreferencesAsync(string userId, CancellationToken ct = default);

    Task SetThemeAsync(string userId, string theme, CancellationToken ct = default);

    Task SetPiecesAsync(string userId, string pieces, CancellationToken ct = default);
}

public sealed class SqliteUserRepository : IUserRepository
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public SqliteUserRepository(IOptions<ChessBotOptions> options, TimeProvider time)
        : this(options.Value.DatabasePath, time)
    {
    }

    public SqliteUserRepository(string databasePath, TimeProvider? time = null)
    {
        _connectionString = Database.ConnectionString(databasePath);
        _time = time ?? TimeProvider.System;
        Database.EnsureSchema(_connectionString);
    }

    public async Task<UserPreferences> GetPreferencesAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = Database.Open(_connectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT theme, pieces FROM users WHERE user_id = $user";
        command.Parameters.AddWithValue("$user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return UserPreferences.None;
        }

        return new UserPreferences(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public Task SetThemeAsync(string userId, string theme, CancellationToken ct = default) =>
        SetAsync(userId, "theme", theme, ct);

    public Task SetPiecesAsync(string userId, string pieces, CancellationToken ct = default) =>
        SetAsync(userId, "pieces", pieces, ct);

    /// <param name="column">A column of the users table; never user input.</param>
    private async Task SetAsync(string userId, string column, string value, CancellationToken ct)
    {
        // The user may not have played yet, so this can create their row.
        await using var connection = Database.Open(_connectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO users (user_id, created_at, {column}) VALUES ($user, $created, $value)
            ON CONFLICT (user_id) DO UPDATE SET {column} = excluded.{column}
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$created", _time.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct);
    }
}

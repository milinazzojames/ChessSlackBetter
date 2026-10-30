using Microsoft.Data.Sqlite;

namespace ChessSlackBetter.Bot;

/// <summary>
/// The SQLite schema shared by the repositories. <see cref="EnsureSchema"/> is safe to run any number of times, so each
/// repository calls it on construction and gets a database that's up to date whichever one is created first.
/// </summary>
public static class Database
{
    public static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static void EnsureSchema(string connectionString)
    {
        using var connection = Open(connectionString);
        using var games = connection.CreateCommand();
        games.CommandText = """
            CREATE TABLE IF NOT EXISTS games (
                channel_id      TEXT NOT NULL,
                thread_ts       TEXT NOT NULL,
                challenger_id   TEXT NOT NULL,
                opponent_id     TEXT NOT NULL,
                white_id        TEXT NOT NULL,
                black_id        TEXT NOT NULL,
                state           TEXT NOT NULL,
                moves           TEXT NOT NULL,
                draw_offered_by TEXT,
                outcome         TEXT NOT NULL,
                termination     TEXT NOT NULL,
                analysis_url    TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                PRIMARY KEY (channel_id, thread_ts)
            );
            CREATE INDEX IF NOT EXISTS ix_games_white ON games (white_id, state);
            CREATE INDEX IF NOT EXISTS ix_games_black ON games (black_id, state);
            """;
        games.ExecuteNonQuery();

        // Columns added after the first release; older databases get them here.
        AddColumnIfMissing(connection, "games", "mode", "TEXT NOT NULL DEFAULT 'Standard'");
        AddColumnIfMissing(connection, "games", "dice", "TEXT");

        // One row per Slack user who has been in a game or set a preference. Players from games saved before this
        // table existed are backfilled here, first seen at their earliest game.
        using var users = connection.CreateCommand();
        users.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                user_id    TEXT NOT NULL PRIMARY KEY,
                created_at TEXT NOT NULL,
                theme      TEXT,
                pieces     TEXT
            );
            INSERT OR IGNORE INTO users (user_id, created_at)
            SELECT user_id, MIN(created_at) FROM (
                SELECT white_id AS user_id, created_at FROM games
                UNION ALL
                SELECT black_id, created_at FROM games
            )
            GROUP BY user_id;
            """;
        users.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $name";
        check.Parameters.AddWithValue("$table", table);
        check.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}

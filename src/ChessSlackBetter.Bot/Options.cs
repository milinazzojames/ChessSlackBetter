namespace ChessSlackBetter.Bot;

public sealed class SlackOptions
{
    public const string Section = "Slack";

    /// <summary>Bot User OAuth Token (xoxb-...).</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Signing Secret from the app's Basic Information page, used to verify requests.</summary>
    public string SigningSecret { get; set; } = "";
}

public sealed class ChessBotOptions
{
    public const string Section = "ChessBot";

    /// <summary>
    /// Public HTTPS base URL of this app (e.g. https://chess.example.com). Slack fetches board images from
    /// {PublicBaseUrl}/board.png. When empty, boards are drawn as text instead.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    public string DatabasePath { get; set; } = "chessbot.db";

    /// <summary>Let players type moves in a game thread without mentioning the bot.</summary>
    public bool AllowMovesWithoutMention { get; set; } = true;

    /// <summary>Import finished games to lichess.org so players get a computer-analysis link.</summary>
    public bool ImportFinishedGamesToLichess { get; set; } = true;

    /// <summary>Chance, from 0 to 1, that the bot posts a philosophical quote after a turn.</summary>
    public double QuoteChance { get; set; } = 0.01;
}

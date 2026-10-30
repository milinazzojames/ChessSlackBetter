using ChessSlackBetter.Bot;
using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Games;
using ChessSlackBetter.Bot.Rendering;
using ChessSlackBetter.Bot.Slack;
using ChessSlackBetter.Bot.Users;
using ChessSlackBetter.Chess;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SlackOptions>(builder.Configuration.GetSection(SlackOptions.Section));
builder.Services.Configure<ChessBotOptions>(builder.Configuration.GetSection(ChessBotOptions.Section));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SlackSignatureVerifier>();
builder.Services.AddHttpClient<ISlackApi, SlackApiClient>();
builder.Services.AddHttpClient<IAnalysisLinks, LichessClient>(http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IGameRepository, SqliteGameRepository>();
builder.Services.AddSingleton<IUserRepository, SqliteUserRepository>();
builder.Services.AddSingleton<BoardRenderer>();
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection(AnthropicOptions.Section));
builder.Services.AddSingleton<IDiceJudge, ClaudeDiceJudge>();
builder.Services.AddSingleton<IDiceRoller, CryptoDiceRoller>();
builder.Services.AddScoped<ChessBotService>();
builder.Services.AddSingleton<WorkQueue>();
builder.Services.AddHostedService<WorkQueueService>();

var app = builder.Build();

app.MapGet("/", () => "ChessSlackBetter is running.");
app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapSlackEndpoints();

// Slack fetches board images from here. The URL encodes the whole position, so responses can be cached forever.
// `last` is a UCI move to highlight; `hl` is a comma-separated list of squares (used after dice rolls);
// `theme` is a BoardTheme name and `pieces` a PieceSet name; unknown or missing names use the defaults.
app.MapGet("/board.png", (string fen, string? last, string? hl, string? pov, string? theme, string? pieces, BoardRenderer renderer, HttpResponse response) =>
{
    Position position;
    try
    {
        position = Position.FromFen(fen);
    }
    catch (FormatException)
    {
        return Results.BadRequest("Invalid FEN.");
    }

    var highlights = new HashSet<int>();
    if (last is { Length: 4 or 5 } &&
        Square.TryParse(last.AsSpan(0, 2), out var from) &&
        Square.TryParse(last.AsSpan(2, 2), out var to))
    {
        highlights.Add(from);
        highlights.Add(to);
    }

    foreach (var name in (hl ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        if (Square.TryParse(name, out var square))
        {
            highlights.Add(square);
        }
    }

    var perspective = pov == "b" ? PieceColor.Black : PieceColor.White;
    response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(renderer.RenderPng(position, highlights, perspective, BoardTheme.Find(theme), PieceSet.Find(pieces)), "image/png");
});

app.Run();

public partial class Program;

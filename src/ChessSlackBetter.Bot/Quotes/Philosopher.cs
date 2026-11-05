using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using ChessSlackBetter.Bot.Dice;
using Microsoft.Extensions.Options;

namespace ChessSlackBetter.Bot.Quotes;

/// <summary>A quote in its original language.</summary>
public sealed record Quote(string Text, string Language, string Attribution);

public interface IPhilosopher
{
    /// <summary>
    /// Now and then (see <see cref="ChessBotOptions.QuoteChance"/>) returns a quote to post after a turn; usually null.
    /// <paramref name="situation"/> describes the game so the quote can, but needn't, fit the position.
    /// </summary>
    Task<Quote?> MaybeQuoteAsync(string situation, CancellationToken ct = default);
}

/// <summary>Asks Claude for a quote in a language picked here, falling back to a fixed list without an API key.</summary>
public sealed class Philosopher(
    IOptions<ChessBotOptions> botOptions,
    IOptions<AnthropicOptions> anthropicOptions,
    ILogger<Philosopher> logger) : IPhilosopher
{
    // Picked in code: asked to choose, a model favours the same few languages.
    private static readonly string[] Languages =
    [
        "Latin", "Ancient Greek", "Modern Greek", "French", "German", "Spanish", "Portuguese", "Italian", "Dutch",
        "Danish", "Swedish", "Norwegian", "Icelandic", "Finnish", "Estonian", "Hungarian", "Polish", "Czech",
        "Russian", "Ukrainian", "Serbian", "Romanian", "Turkish", "Arabic", "Persian", "Hebrew", "Hindi", "Sanskrit",
        "Bengali", "Tamil", "Urdu", "Mandarin Chinese", "Classical Chinese", "Japanese", "Korean", "Vietnamese",
        "Thai", "Indonesian", "Tagalog", "Swahili", "Yoruba", "Zulu", "Amharic", "Irish", "Welsh", "Basque",
        "Catalan", "Esperanto", "Maori", "Hawaiian", "Quechua",
    ];

    private const string SystemPrompt = """
        You are the resident philosopher of a chess bot in Slack. Once in a while, after someone's turn, you post a
        single deep quote. It can be any kind of quote: philosophy, poetry, scripture, a proverb, a line from
        literature, or an aphorism of your own. Profound is good; a little wry is fine too.

        You are told the language to use and what is happening in the game. If something in the game suggests a
        fitting theme (a sacrifice, a lost queen, a king under attack, a slow quiet position), you may choose a quote
        that resonates with it, but you don't have to, and never mention chess moves or squares in the quote itself.

        Accuracy matters: only attribute a quote to a real person or work if you are confident it is genuine and
        correctly worded. Otherwise use a traditional proverb you are sure of, or write an original aphorism in that
        language and attribute it to "ChessBetterBot". Write the quote in the language's usual script.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "The quote in the requested language" },
            "attribution": { "type": "string", "description": "Who said or wrote it, e.g. \"Seneca\", \"Japanese proverb\", \"ChessBetterBot\"" }
          },
          "required": ["text", "attribution"],
          "additionalProperties": false
        }
        """)!;

    /// <summary>Used when there is no API key or Claude can't be reached.</summary>
    public static readonly IReadOnlyList<Quote> Fallback =
    [
        new("Πάντα ῥεῖ.", "Ancient Greek", "attributed to Heraclitus"),
        new("Memento mori.", "Latin", "Latin saying"),
        new("Dum spiro, spero.", "Latin", "Latin motto"),
        new("Je pense, donc je suis.", "French", "René Descartes"),
        new("L'enfer, c'est les autres.", "French", "Jean-Paul Sartre, Huis clos"),
        new("Was mich nicht umbringt, macht mich stärker.", "German", "Friedrich Nietzsche"),
        new("Livet forstås baglæns, men må leves forlæns.", "Danish", "Søren Kierkegaard"),
        new("Caminante, no hay camino, se hace camino al andar.", "Spanish", "Antonio Machado"),
        new("Tudo vale a pena se a alma não é pequena.", "Portuguese", "Fernando Pessoa"),
        new("Красота спасёт мир.", "Russian", "Fyodor Dostoevsky, The Idiot"),
        new("千里之行，始於足下。", "Classical Chinese", "Laozi, Tao Te Ching"),
        new("七転び八起き", "Japanese", "Japanese proverb"),
        new("Haba na haba hujaza kibaba.", "Swahili", "Swahili proverb"),
        new("सत्यमेव जयते", "Sanskrit", "Mundaka Upanishad"),
        new("Ars longa, vita brevis.", "Latin", "Hippocrates, in Latin translation"),
    ];

    public async Task<Quote?> MaybeQuoteAsync(string situation, CancellationToken ct = default)
    {
        if (Random.Shared.NextDouble() >= botOptions.Value.QuoteChance)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(anthropicOptions.Value.ApiKey))
        {
            return Fallback[Random.Shared.Next(Fallback.Count)];
        }

        var language = Languages[Random.Shared.Next(Languages.Length)];
        try
        {
            return await AskAsync(language, situation, ct) ?? Fallback[Random.Shared.Next(Fallback.Count)];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Couldn't get a quote from Claude; using a stored one");
            return Fallback[Random.Shared.Next(Fallback.Count)];
        }
    }

    private async Task<Quote?> AskAsync(string language, string situation, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = anthropicOptions.Value.ApiKey };
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = anthropicOptions.Value.Model,
            MaxTokens = 4000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = SystemPrompt,
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat { Schema = Schema },
            },
            Messages = [new BetaUserMessageParam { Content = $"Language: {language}\nThe game: {situation}" }],
        }, ct);

        if (response.StopReason == "refusal")
        {
            return null;
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        var dto = JsonSerializer.Deserialize<QuoteDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return dto is null || string.IsNullOrWhiteSpace(dto.Text)
            ? null
            : new Quote(dto.Text.Trim(), language, dto.Attribution.Trim());
    }

    private sealed record QuoteDto(string Text, string Attribution);
}

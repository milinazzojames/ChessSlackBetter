using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Chess;

namespace ChessSlackBetter.Bot.Games;

/// <summary>
/// Custom rules: lasting rules the referee runs itself each time they fire, for anything the fixed effects can't
/// express. Code still checks every board change the referee asks for.
/// </summary>
public sealed partial class ChessBotService
{
    /// <summary>Longest memory a custom rule may keep, so a runaway rule can't bloat the game record.</summary>
    private const int MaxRuleMemory = 4000;

    private const int RandomNumbersPerFiring = 12;

    /// <summary>
    /// Fires the custom rules that match the turn <paramref name="moverId"/> just took. Returns lines to post;
    /// any questions they ask are added to the dice state.
    /// </summary>
    private async Task<List<string>> FireCustomRulesAsync(GameRecord record, Game game, string moverId, CancellationToken ct)
    {
        var notes = new List<string>();
        if (record.Mode != GameMode.DiceRoll)
        {
            return notes;
        }

        foreach (var rule in record.Dice.Rules.ToList())
        {
            if (game.IsOver || !rule.IsCustom || !rule.Matches(moverId))
            {
                continue;
            }

            rule.TurnsSeen++;
            if (rule.TurnsSeen % rule.Every != 0)
            {
                continue;
            }

            var mover = record.ColorOf(moverId).ToString().ToLowerInvariant();
            var last = game.LastMove is { } move ? move.SanWithRules : "nothing yet";
            await RunCustomRuleAsync(record, game, rule, $"The rule fires: {mover} just finished their turn (last move: {last}).", notes, ct);
            if (record.Dice.Rules.Contains(rule))
            {
                CountFiring(record, rule, notes);
            }
        }

        return notes;
    }

    private async Task RunCustomRuleAsync(GameRecord record, Game game, PassiveRule rule, string happened, List<string> notes, CancellationToken ct)
    {
        CustomRuleResult result;
        try
        {
            var randoms = Enumerable.Range(0, RandomNumbersPerFiring).Select(_ => dice.Pick(100) + 1).ToList();
            result = await judge.RunCustomRuleAsync(new CustomRuleCall(
                game.Current, rule.Custom, ColorOfOwner(record, rule), rule.Memory, happened, randoms, record.Dice.Damage), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Custom rule failed: {Rule}", rule.Custom);
            notes.Add($":sparkles: *Rule skipped:* {rule.Summary} _(the referee couldn't be reached this turn)_");
            return;
        }

        rule.Memory = result.Memory.Length > MaxRuleMemory ? result.Memory[..MaxRuleMemory] : result.Memory;
        rule.Status = result.Status.Trim();

        var line = $":sparkles: *{rule.Summary}*";
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            line += "\n> " + result.Message.Trim().Replace("\n", "\n> ");
        }

        if (result.Effects.Count > 0)
        {
            line += TryFireRule(game, record.Dice, result.Effects, out var log)
                ? LogText(log)
                : "\n_(its board changes weren't legal right now, so they were skipped)_";
        }

        notes.Add(line);

        if (result.Expire)
        {
            record.Dice.Rules.Remove(rule);
            record.Dice.Riddles.RemoveAll(r => r.RuleId == rule.Id);
            notes.Add($"_\"{rule.Summary}\" has ended._");
            return;
        }

        if (!game.IsOver && !string.IsNullOrWhiteSpace(result.Question) && TryParseSide(result.AskColor, out var asked) &&
            record.Dice.Riddles.All(r => r.RuleId != rule.Id))
        {
            var solverId = record.PlayerFor(asked);
            record.Dice.Riddles.Add(new PendingRiddle
            {
                SolverId = solverId,
                Question = result.Question.Trim(),
                Answer = "",
                RuleId = rule.Id,
                Summary = rule.Summary,
            });
        }
    }

    /// <summary>Hands a player's reply back to the custom rule that asked for it.</summary>
    private async Task AnswerCustomQuestionAsync(GameRecord record, Game game, string userId, PendingRiddle question, string answer, CancellationToken ct)
    {
        record.Dice.Riddles.Remove(question);
        var notes = new List<string>();
        if (record.Dice.Rules.FirstOrDefault(r => r.Id == question.RuleId) is { } rule)
        {
            var who = record.ColorOf(userId).ToString().ToLowerInvariant();
            await RunCustomRuleAsync(record, game, rule,
                $"{who} replied to your question \"{question.Question}\". Their reply: <reply>{answer.Trim()}</reply>", notes, ct);
        }
        else
        {
            notes.Add("_That rule has already ended, so the reply doesn't change anything._");
        }

        record.SyncFrom(game);
        await SaveAsync(record, ct);
        await PostRuleNotesAsync(record, notes, ct);
        await PostPendingQuestionsAsync(record, ct);

        if (game.IsOver)
        {
            await FinishAsync(record, game, ct);
        }
        else if (record.Dice.RiddleFor(userId) is null)
        {
            await PostBoardAsync(record, game, ct);
        }
    }

    private static PieceColor ColorOfOwner(GameRecord record, PassiveRule rule) => record.ColorOf(rule.OwnerId);

    private static bool TryParseSide(string text, out PieceColor color)
    {
        color = text.Equals("black", StringComparison.OrdinalIgnoreCase) ? PieceColor.Black : PieceColor.White;
        return text.Equals("white", StringComparison.OrdinalIgnoreCase) || text.Equals("black", StringComparison.OrdinalIgnoreCase);
    }
}

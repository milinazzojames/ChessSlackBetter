using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Slack;
using ChessSlackBetter.Chess.ThreePlayer;

namespace ChessSlackBetter.Bot.Games;

/// <summary>
/// Three-player ChessDiceRoll: the same gambles as two-player dice games, but each of the two opponents gets a veto,
/// and damage to one opponent counts for half because it also helps the third player (see TriDiceRules.MaterialDelta).
/// </summary>
public sealed partial class ChessBotService
{
    private async Task RequestThreePlayerRollAsync(ThreePlayerRecord record, string userId, string request, Func<string, Task> reply, CancellationToken ct)
    {
        if (!record.WithDice)
        {
            await reply("Gambles are only for dice games. Start one with `@ChessBetterBot threeplayerdice @someone @someone`.");
            return;
        }

        if (await ActiveThreePlayerGameAsync(record, userId, reply) is not { } game)
        {
            return;
        }

        var player = record.ColorOf(userId);
        if (game.Current.SideToMove != player)
        {
            await reply($"You can only gamble on your own turn — waiting on <@{record.PlayerFor(game.Current.SideToMove)}>.");
            return;
        }

        if (record.Dice.Pending is not null)
        {
            await reply("You already have a gamble waiting. Roll or cancel it first.");
            return;
        }

        if (record.Dice.RollsLeft(userId) <= 0)
        {
            await reply($"You've used all {DiceLimits.RollsPerPlayer} of your rolls this game. Time to play it straight!");
            return;
        }

        DiceRuling ruling;
        try
        {
            ruling = await judge.JudgeThreePlayerAsync(game.Current, player, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dice referee failed for {Request}", request);
            await reply("The dice referee is unavailable right now, so I can't price that gamble. Make a normal move or try again later.");
            return;
        }

        if (!ruling.Accepted)
        {
            await reply($":game_die: {ruling.Message}");
            return;
        }

        if (!TryPrepareTriOutcomes(game.Current, ruling.SuccessOptions, ruling.SuccessSummary, "If you win", out var successOutcomes, out var error) ||
            !TryPrepareTriOutcomes(game.Current, ruling.FailureOptions, ruling.FailureSummary, "If you lose", out var failureOutcomes, out error))
        {
            await reply($":game_die: I can't set up that gamble. {error}");
            return;
        }

        var gain = Math.Max(0, AverageTriMaterial(game.Current, successOutcomes, player) + ClampBonus(ruling.SuccessPositionalBonus));
        var loss = -(AverageTriMaterial(game.Current, failureOutcomes, player) + ClampBonus(ruling.FailurePositionalBonus));
        if (loss <= 0 || failureOutcomes.Any(o => o.Edits.Count == 0))
        {
            await reply(":game_die: Losing has to cost you something. Add a real penalty, e.g. `…and if I fail I lose my knight`.");
            return;
        }

        var proposal = new DiceProposal
        {
            RequesterId = userId,
            Request = request,
            SuccessOutcomes = successOutcomes,
            FailureOutcomes = failureOutcomes,
            SuccessSummary = ruling.SuccessSummary,
            FailureSummary = ruling.FailureSummary,
            Target = DiceRules.FairTarget(gain, loss),
            Reasoning = ruling.Reasoning,
        };

        record.Dice.Pending = proposal;
        var content = Messages.ThreePlayerDiceProposal(record, game, proposal);
        proposal.MessageTs = await slack.PostMessageAsync(new SlackMessage(record.ChannelId, content.Text, content.Blocks, record.ThreadTs), ct);
        await SaveThreePlayerAsync(record, ct);
    }

    private async Task ConfirmThreePlayerRollAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.Dice.Pending is not { } proposal)
        {
            await reply("There's no gamble waiting to be rolled.");
            return;
        }

        if (proposal.RequesterId != userId)
        {
            await reply($"Only <@{proposal.RequesterId}> can roll their gamble.");
            return;
        }

        if (await ActiveThreePlayerGameAsync(record, userId, reply) is not { } game)
        {
            return;
        }

        var roll = dice.Roll();
        var success = roll >= proposal.Target;
        var outcomes = success ? proposal.SuccessOutcomes : proposal.FailureOutcomes;
        var picked = outcomes.Count == 0 ? null : outcomes[dice.Pick(outcomes.Count)];
        var eliminated = game.Eliminations.Count;

        record.Dice.Pending = null;
        string? error = "No outcomes were stored for this gamble.";
        if (picked is null || !game.TryApplyEdits(picked.Edits.Select(TriSquareEdit.Parse).ToList(), out error))
        {
            // The board can't have changed since the proposal, so this shouldn't happen; don't charge a roll for it.
            logger.LogError("Approved gamble could not be applied: {Error}", error);
            await SaveThreePlayerAsync(record, ct);
            await CloseThreePlayerProposalAsync(record, proposal, "_Something went wrong applying this gamble, so it was called off. No roll used._", ct);
            return;
        }

        var outcome = Messages.DiceRollOutcome(proposal, roll, success, picked.Summary);
        record.Dice.RollsUsed[userId] = record.Dice.RollsUsed.GetValueOrDefault(userId) + 1;
        record.Dice.Log.Add(new DiceRollResult(userId, proposal.Request, proposal.Target, roll, success, picked.Summary));
        record.DrawAgreed.Clear();

        await CloseThreePlayerProposalAsync(record, proposal, outcome, ct);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, $"<@{userId}> {outcome}", ThreadTs: record.ThreadTs), ct);
        await AfterThreePlayerTurnAsync(record, game, eliminated, ct);
    }

    private async Task CancelThreePlayerRollAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.Dice.Pending is not { } proposal)
        {
            await reply("There's no gamble to cancel.");
            return;
        }

        if (proposal.RequesterId != userId)
        {
            await reply(record.IsPlayer(userId)
                ? "Only the player who proposed the gamble can cancel it. You can veto it instead."
                : $"Only <@{proposal.RequesterId}> can cancel their gamble.");
            return;
        }

        record.Dice.Pending = null;
        await SaveThreePlayerAsync(record, ct);
        await CloseThreePlayerProposalAsync(record, proposal, $"_Cancelled by <@{userId}>. No roll used — still their move._", ct);
    }

    /// <summary>Either opponent still in the game can veto, once per game each.</summary>
    private async Task VetoThreePlayerAsync(ThreePlayerRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.Dice.Pending is not { } proposal)
        {
            await reply("There's no gamble to veto.");
            return;
        }

        if (proposal.RequesterId == userId)
        {
            await reply("You can't veto your own gamble. Cancel it instead.");
            return;
        }

        if (await ActiveThreePlayerGameAsync(record, userId, reply) is null)
        {
            return;
        }

        if (record.Dice.VetoesLeft(userId) <= 0)
        {
            await reply("You've already used your veto this game.");
            return;
        }

        record.Dice.Pending = null;
        record.Dice.VetoesUsed.Add(userId);
        await SaveThreePlayerAsync(record, ct);
        await CloseThreePlayerProposalAsync(record, proposal,
            $":no_entry: *Vetoed* by <@{userId}>. No roll used — <@{proposal.RequesterId}>, it's still your move.", ct);
    }

    private async Task CloseThreePlayerProposalAsync(ThreePlayerRecord record, DiceProposal proposal, string outcome, CancellationToken ct)
    {
        if (proposal.MessageTs is null)
        {
            return;
        }

        var closed = Messages.DiceProposalClosed(proposal, outcome);
        await slack.UpdateMessageAsync(record.ChannelId, proposal.MessageTs, closed.Text, closed.Blocks, ct);
    }

    /// <summary>Checks each option and keeps the playable ones, as for two-player gambles.</summary>
    private static bool TryPrepareTriOutcomes(
        TriPosition position,
        IReadOnlyList<DiceOption> options,
        string overallSummary,
        string label,
        out List<DiceOutcome> outcomes,
        out string? error)
    {
        outcomes = [];
        error = $"{label}: the referee didn't describe what happens.";
        string? firstError = null;
        foreach (var option in options)
        {
            if (TriDiceRules.TryBuildEdits(position, option.Effects, out var edits, out var optionError) &&
                TriGame.ValidateEdits(position, edits, out optionError))
            {
                var summary = string.IsNullOrWhiteSpace(option.Summary) ? overallSummary : option.Summary;
                outcomes.Add(new DiceOutcome(edits.Select(e => e.ToString()).ToList(), summary));
            }
            else
            {
                firstError ??= optionError;
            }
        }

        if (outcomes.Count > 0)
        {
            error = null;
            return true;
        }

        if (firstError is not null)
        {
            error = $"{label}: {firstError}";
        }

        return false;
    }

    private static double AverageTriMaterial(TriPosition position, List<DiceOutcome> outcomes, TriColor player) =>
        outcomes.Average(o => TriDiceRules.MaterialDelta(position, o.Edits.Select(TriSquareEdit.Parse), player));
}

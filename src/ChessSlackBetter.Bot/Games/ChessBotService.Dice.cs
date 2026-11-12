using ChessSlackBetter.Bot.Dice;
using ChessSlackBetter.Bot.Slack;
using ChessSlackBetter.Chess;

namespace ChessSlackBetter.Bot.Games;

/// <summary>ChessDiceRoll: on their turn a player may gamble on a d20 instead of moving.</summary>
public sealed partial class ChessBotService
{
    private async Task RequestRollAsync(GameRecord record, string userId, string request, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.Mode != GameMode.DiceRoll)
        {
            await reply("Gambles are only for ChessDiceRoll games. Start one with `@ChessBetterBot diceroll @someone`.");
            return;
        }

        if (!await EnsureActivePlayerAsync(record, userId, reply))
        {
            return;
        }

        var game = record.LoadGame();
        if (game.Current.SideToMove != record.ColorOf(userId))
        {
            await reply($"You can only gamble on your own turn — waiting on <@{record.PlayerFor(game.Current.SideToMove)}>.");
            return;
        }

        if (record.Dice.Pending is not null)
        {
            await reply("You already have a gamble waiting. Roll or cancel it first.");
            return;
        }

        if (await OwesRiddleAsync(record, userId, reply))
        {
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
            ruling = await judge.JudgeAsync(game.Current, record.ColorOf(userId), request, ct);
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

        if (!TryPrepareOutcomes(game.Current, ruling.SuccessOptions, ruling.SuccessSummary, "If you win", dice.Pick, out var successOutcomes, out var error) ||
            !TryPrepareOutcomes(game.Current, ruling.FailureOptions, ruling.FailureSummary, "If you lose", dice.Pick, out var failureOutcomes, out error))
        {
            await reply($":game_die: I can't set up that gamble. {error}");
            return;
        }

        // Random outcomes are priced on their average. A pointless win is allowed; it just makes the roll easy.
        var player = record.ColorOf(userId);
        var gain = Math.Max(0, AverageMaterial(game.Current, successOutcomes, player) + ClampBonus(ruling.SuccessPositionalBonus));
        var loss = -(AverageMaterial(game.Current, failureOutcomes, player) + ClampBonus(ruling.FailurePositionalBonus));
        if (loss <= 0 || failureOutcomes.Any(o => o.ChangesNothing))
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
        var content = Messages.DiceProposal(record, proposal);
        proposal.MessageTs = await slack.PostMessageAsync(new SlackMessage(record.ChannelId, content.Text, content.Blocks, record.ThreadTs), ct);
        await SaveAsync(record, ct);
    }

    private async Task ConfirmRollAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
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

        if (!await EnsureActivePlayerAsync(record, userId, reply))
        {
            return;
        }

        var game = record.LoadGame();
        var before = game.Current;
        var roll = dice.RollD20();
        var success = roll >= proposal.Target;
        var outcomes = success ? proposal.SuccessOutcomes : proposal.FailureOutcomes;
        var picked = outcomes.Count == 0 ? null : outcomes[dice.Pick(outcomes.Count)];

        record.Dice.Pending = null;
        string? error = "No outcomes were stored for this gamble.";
        // A resignation changes nothing on the board, so it isn't checked as an edit: the requester may well be in
        // check (that's usually why they're betting the game), which an empty edit would count as illegal.
        if (picked is null || (!picked.Resigns && !game.TryApplyEdits(picked.Edits.Select(SquareEdit.Parse).ToList(), out error)))
        {
            // The board can't have changed since the proposal, so this shouldn't happen; don't charge a roll for it.
            logger.LogError("Approved gamble could not be applied: {Error}", error);
            await SaveAsync(record, ct);
            await CloseProposalAsync(record, proposal, "_Something went wrong applying this gamble, so it was called off. No roll used._", ct);
            return;
        }

        FollowDamage(record, before, game.Current);
        if (picked.Resigns)
        {
            game.Resign(record.ColorOf(userId));
        }

        var outcome = Messages.DiceRollOutcome(proposal, roll, success, picked.Summary);
        record.Dice.RollsUsed[userId] = record.Dice.RollsUsed.GetValueOrDefault(userId) + 1;
        record.Dice.Log.Add(new DiceRollResult(userId, proposal.Request, proposal.Target, roll, success, picked.Summary));

        // Rules already in play fire for this turn; the new ones start with the next matching turn.
        var ruleNotes = FireRules(record, game, userId);
        ruleNotes.AddRange(await FireCustomRulesAsync(record, game, userId, ct));
        if (!game.IsOver)
        {
            record.Dice.Rules.AddRange((picked.Rules ?? []).Select(spec => PassiveRule.From(spec, userId)));
        }

        record.SyncFrom(game);
        record.DrawOfferedBy = null;
        await SaveAsync(record, ct);

        await CloseProposalAsync(record, proposal, outcome, ct);
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, $"<@{userId}> {outcome}", ThreadTs: record.ThreadTs), ct);
        await PostRuleNotesAsync(record, ruleNotes, ct);
        await PostPendingQuestionsAsync(record, ct);

        if (game.IsOver)
        {
            await FinishAsync(record, game, ct);
        }
        else
        {
            await PostBoardAsync(record, game, ct);
            await AskRiddlesAsync(record, game, ct);
        }

        await MaybeQuoteAsync(record, game, ct);
    }

    private async Task CancelRollAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
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
        await SaveAsync(record, ct);
        await CloseProposalAsync(record, proposal, $"_Cancelled by <@{userId}>. No roll used — still their move._", ct);
    }

    private async Task VetoAsync(GameRecord record, string userId, Func<string, Task> reply, CancellationToken ct)
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

        if (!record.IsPlayer(userId))
        {
            await reply("Only the players can veto a gamble.");
            return;
        }

        if (record.Dice.VetoesLeft(userId) <= 0)
        {
            await reply("You've already used your veto this game.");
            return;
        }

        record.Dice.Pending = null;
        record.Dice.VetoesUsed.Add(userId);
        await SaveAsync(record, ct);
        await CloseProposalAsync(record, proposal,
            $":no_entry: *Vetoed* by <@{userId}>. No roll used — <@{proposal.RequesterId}>, it's still your move.", ct);
    }

    private async Task CloseProposalAsync(GameRecord record, DiceProposal proposal, string outcome, CancellationToken ct)
    {
        if (proposal.MessageTs is null)
        {
            return;
        }

        var closed = Messages.DiceProposalClosed(proposal, outcome);
        await slack.UpdateMessageAsync(record.ChannelId, proposal.MessageTs, closed.Text, closed.Blocks, ct);
    }

    /// <summary>
    /// Checks each option and keeps the ones that can be played. For a random outcome, an impossible option is
    /// just dropped from the draw; the gamble is refused only if none are left.
    /// </summary>
    private static bool TryPrepareOutcomes(
        Position position,
        IReadOnlyList<DiceOption> options,
        string overallSummary,
        string label,
        Func<int, int> pick,
        out List<DiceOutcome> outcomes,
        out string? error)
    {
        outcomes = [];
        error = $"{label}: the referee didn't describe what happens.";
        string? firstError = null;
        foreach (var option in options)
        {
            var summary = string.IsNullOrWhiteSpace(option.Summary) ? overallSummary : option.Summary;
            if (option.Effects.Any(DiceRules.IsResign))
            {
                // Resigning ends the game, so nothing else in the option matters. Only the requester may resign:
                // making the opponent resign would just be winning the game on a roll.
                var requester = position.SideToMove.ToString();
                if (option.Effects.Where(DiceRules.IsResign).All(e => e.Color == "" || e.Color.Equals(requester, StringComparison.OrdinalIgnoreCase)))
                {
                    outcomes.Add(new DiceOutcome([], summary, Resigns: true));
                }
                else
                {
                    firstError ??= "Dice can't make your opponent resign; that would win the game outright.";
                }
            }
            else if (TryPrepareOption(position, option, pick, out var edits, out var optionError))
            {
                var rules = option.Rules is { Count: > 0 } r ? r.ToList() : null;
                outcomes.Add(new DiceOutcome(edits.Select(e => e.ToString()).ToList(), summary, rules));
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

    private static bool TryPrepareOption(Position position, DiceOption option, Func<int, int> pick, out List<SquareEdit> edits, out string? error)
    {
        edits = [];
        if (!DiceRules.TryBuildEdits(position, option.Effects, out var built, out error, pick) ||
            !Game.ValidateEdits(position, built, out error))
        {
            return false;
        }

        // Fine if the player could already force that mate with normal moves.
        if (position.WithEdits(built).IsMatedWithin(2) && !position.CanForceMate(2))
        {
            error = "Dice can't set up a checkmate within two moves.";
            return false;
        }

        // A rule has to be able to fire at least sometimes; one that never could is a mistake by the referee.
        foreach (var rule in option.Rules ?? [])
        {
            if (!CanEverFire(position.WithEdits(built), rule, out error))
            {
                error = $"The rule \"{rule.Summary}\" can't work: {error}";
                return false;
            }
        }

        edits = built;
        return true;
    }

    private static bool CanEverFire(Position position, DiceRuleSpec rule, out string? error)
    {
        error = "it has no effects.";
        if (rule.IsCustom)
        {
            // The referee decides what a custom rule does each time, so there's nothing to check up front.
            error = null;
            return true;
        }

        var lists = (rule.IsRiddle ? [rule.CorrectEffects ?? [], rule.Effects] : new[] { rule.Effects })
            .Where(l => l.Count > 0)
            .ToList();
        if (lists.Count == 0)
        {
            // A riddle with nothing riding on it is pointless but harmless.
            return rule.IsRiddle;
        }

        // The gamble is made on the requester's turn, so they're the side to move.
        var random = new Random(4321);
        foreach (var target in DiceRules.TargetsOf(rule.Trigger, position.SideToMove))
        {
            foreach (var effects in lists.Select(l => DiceRules.ForTarget(l, target)))
            {
                for (var i = 0; i < 24; i++)
                {
                    if (DiceRules.TryBuildEdits(position, effects, out var edits, out error, random.Next) &&
                        ValidatePieceEdits(position, edits, out error))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Just the piece checks (no kings, no pawns on their promotion rank), whoever is to move.</summary>
    private static bool ValidatePieceEdits(Position position, List<SquareEdit> edits, out string? error)
    {
        error = edits.Any(e => position[e.Square].Type == PieceType.King || e.Piece.Type == PieceType.King)
            ? "kings can't be changed by dice."
            : edits.Any(e => e.Piece.Type == PieceType.Pawn && Square.Rank(e.Square) == (e.Piece.Color == PieceColor.White ? 7 : 0))
                ? "pawns can't be placed on the rank where they would promote."
                : null;
        return error is null;
    }

    /// <summary>Average material change plus the expected value of any passive rules the outcome brings in.</summary>
    private static double AverageMaterial(Position position, List<DiceOutcome> outcomes, PieceColor player) =>
        outcomes.Average(o =>
        {
            if (o.Resigns)
            {
                return -DiceRules.ResignCost(position, player);
            }

            var edits = o.Edits.Select(SquareEdit.Parse).ToList();
            var after = position.WithEdits(edits);
            return DiceRules.MaterialDelta(position, edits, player) +
                   (o.Rules ?? []).Sum(rule => DiceRules.EstimateRuleValue(after, rule, player));
        });

    private static double ClampBonus(double bonus) =>
        Math.Clamp(bonus, -DiceRules.MaxPositionalBonus, DiceRules.MaxPositionalBonus);

    /// <summary>How many times a rule tries random picks before skipping a firing.</summary>
    private const int RuleFiringAttempts = 8;

    /// <summary>
    /// Fires the plain passive rules that match the turn <paramref name="moverId"/> just took, editing the board
    /// without passing the turn. Returns a line for each rule that fired or had to skip. Riddle rules are asked
    /// separately, by <see cref="AskRiddlesAsync"/>.
    /// </summary>
    private List<string> FireRules(GameRecord record, Game game, string moverId)
    {
        var notes = new List<string>();
        var target = record.ColorOf(moverId);
        foreach (var rule in record.Dice.Rules.ToList())
        {
            if (game.IsOver || rule.IsRiddle || rule.IsCustom || !rule.Matches(moverId))
            {
                continue;
            }

            rule.TurnsSeen++;
            if (rule.TurnsSeen % rule.Every != 0)
            {
                continue;
            }

            notes.Add(TryFireRule(game, record.Dice, DiceRules.ForTarget(rule.Effects, target), out var log)
                ? $":repeat: *Rule:* {rule.Summary}{LogText(log)}"
                : $":repeat: *Rule skipped:* {rule.Summary} _(no legal way to do it this turn)_");

            CountFiring(record, rule, notes);
        }

        return notes;
    }

    private static void CountFiring(GameRecord record, PassiveRule rule, List<string> notes)
    {
        if (rule.TimesLeft is { } left)
        {
            rule.TimesLeft = left - 1;
            if (rule.TimesLeft <= 0)
            {
                record.Dice.Rules.Remove(rule);
                notes.Add($"_\"{rule.Summary}\" has run out._");
            }
        }
    }

    private static string LogText(List<string> log) => log.Count == 0 ? "" : "\n> " + string.Join("\n> ", log);

    private async Task PostRuleNotesAsync(GameRecord record, List<string> notes, CancellationToken ct)
    {
        if (notes.Count > 0)
        {
            await slack.PostMessageAsync(new SlackMessage(record.ChannelId, string.Join('\n', notes), ThreadTs: record.ThreadTs), ct);
        }
    }

    /// <summary>
    /// Applies rule effects after the last turn, retrying random picks a few times. Damage is read from and saved
    /// to <paramref name="state"/>. A firing that only damages (or misses) still counts as fired.
    /// </summary>
    private bool TryFireRule(Game game, DiceState state, IReadOnlyList<DiceEffect> effects, out List<string> log)
    {
        log = [];
        var before = game.Current;
        for (var attempt = 0; attempt < RuleFiringAttempts; attempt++)
        {
            var context = new EffectContext(state.Damage);
            if (!DiceRules.TryBuildEdits(before, effects, out var edits, out _, dice.Pick, context))
            {
                continue;
            }

            if (edits.Count == 0)
            {
                state.Damage = context.Damage;
                log = context.Log;
                return true;
            }

            if (!Game.ValidateAfterTurnEdits(before, edits, out _))
            {
                continue;
            }

            // Like gambles, rules can't hand anyone a checkmate within two moves they didn't already have.
            var after = before.WithEdits(edits, passTurn: false);
            if ((after.IsMatedWithin(2) && !before.IsMatedWithin(2)) || (after.CanForceMate(2) && !before.CanForceMate(2)))
            {
                continue;
            }

            if (game.TryApplyAfterTurn(edits, out _))
            {
                state.Damage = DiceRules.FollowDamage(before, game.Current, context.Damage);
                log = context.Log;
                return true;
            }
        }

        return false;
    }

    /// <summary>Carries damage along with the pieces after a move or gamble changed the board.</summary>
    private static void FollowDamage(GameRecord record, Position before, Position after)
    {
        if (record.Dice.Damage.Count > 0)
        {
            record.Dice.Damage = DiceRules.FollowDamage(before, after, record.Dice.Damage);
        }
    }

    /// <summary>
    /// After a turn, asks the player now to move a riddle for each riddle rule that matches their turn. They have
    /// to answer before they can move or gamble.
    /// </summary>
    private async Task AskRiddlesAsync(GameRecord record, Game game, CancellationToken ct)
    {
        if (record.Mode != GameMode.DiceRoll || game.IsOver)
        {
            return;
        }

        var solver = game.Current.SideToMove;
        var solverId = record.PlayerFor(solver);
        var notes = new List<string>();
        var asked = new List<PendingRiddle>();
        foreach (var rule in record.Dice.Rules.ToList())
        {
            if (!rule.IsRiddle || rule.IsCustom || !rule.Matches(solverId))
            {
                continue;
            }

            rule.TurnsSeen++;
            if (rule.TurnsSeen % rule.Every != 0)
            {
                continue;
            }

            Riddle riddle;
            try
            {
                riddle = await judge.WriteRiddleAsync(rule.Theme, record.Dice.RiddleAnswers, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't write a riddle for rule {Rule}", rule.Summary);
                notes.Add($":jigsaw: *Riddle skipped:* I couldn't come up with one this time. ({rule.Summary})");
                CountFiring(record, rule, notes);
                continue;
            }

            var pending = new PendingRiddle
            {
                SolverId = solverId,
                Question = riddle.Question,
                Answer = riddle.Answer,
                AlsoAccepted = riddle.AlsoAccepted.ToList(),
                CorrectEffects = DiceRules.ForTarget(rule.CorrectEffects, solver),
                WrongEffects = DiceRules.ForTarget(rule.Effects, solver),
                Summary = rule.Summary,
            };
            record.Dice.Riddles.Add(pending);
            record.Dice.RiddleAnswers.Add(riddle.Answer);
            asked.Add(pending);
            CountFiring(record, rule, notes);
        }

        if (asked.Count == 0 && notes.Count == 0)
        {
            return;
        }

        await SaveAsync(record, ct);
        await PostRuleNotesAsync(record, notes, ct);
        await PostPendingQuestionsAsync(record, ct);
    }

    /// <summary>
    /// Posts the riddles and rule questions that haven't been posted yet and records when each was asked, so only
    /// replies sent after the question count as answers.
    /// </summary>
    private async Task PostPendingQuestionsAsync(GameRecord record, CancellationToken ct)
    {
        var unposted = record.Dice.Riddles.Where(r => r.AskedTs is null).ToList();
        if (unposted.Count == 0)
        {
            return;
        }

        foreach (var question in unposted)
        {
            var text = question.RuleId is null
                ? Messages.RiddleQuestion(question)
                : $":question: <@{question.SolverId}>, {question.Question}\n_Reply in the thread before your next move._";
            question.AskedTs = await slack.PostMessageAsync(new SlackMessage(record.ChannelId, text, ThreadTs: record.ThreadTs), ct);
        }

        await SaveAsync(record, ct);
    }

    private async Task AnswerRiddleAsync(GameRecord record, string userId, string answer, string messageTs, Func<string, Task> reply, CancellationToken ct)
    {
        if (record.State != GameState.Active || record.Dice.RiddleFor(userId) is not { } riddle)
        {
            await reply("There's no riddle waiting for you.");
            return;
        }

        if (!riddle.IsAnsweredBy(messageTs))
        {
            // Sent before the question was posted (e.g. while it was still being written), so it can't be an answer.
            await reply($"That was sent before the question was asked, so it doesn't count. Reply to it now: _{riddle.Question}_");
            return;
        }

        var game = record.LoadGame();
        if (riddle.RuleId is not null)
        {
            await AnswerCustomQuestionAsync(record, game, userId, riddle, answer, ct);
            return;
        }

        var correct = Messages.IsRiddleAnswer(answer, riddle);
        var effects = correct ? riddle.CorrectEffects : riddle.WrongEffects;
        var result = effects.Count == 0
            ? ""
            : TryFireRule(game, record.Dice, effects, out var log)
                ? LogText(log)
                : "\n_…but there was no legal way to do that this turn, so nothing happens._";

        record.Dice.Riddles.Remove(riddle);
        record.SyncFrom(game);
        await SaveAsync(record, ct);

        var verdict = correct
            ? $":white_check_mark: <@{userId}> answered *{answer.Trim()}*: correct! The answer was *{riddle.Answer}*."
            : $":x: <@{userId}> answered *{answer.Trim()}*: wrong! The answer was *{riddle.Answer}*.";
        await slack.PostMessageAsync(new SlackMessage(record.ChannelId, verdict + result, ThreadTs: record.ThreadTs), ct);

        if (game.IsOver)
        {
            await FinishAsync(record, game, ct);
        }
        else if (record.Dice.RiddleFor(userId) is null)
        {
            await PostBoardAsync(record, game, ct);
        }
    }

    /// <summary>Replies and returns true when the player still owes a riddle answer, so can't move or gamble yet.</summary>
    private static async Task<bool> OwesRiddleAsync(GameRecord record, string userId, Func<string, Task> reply)
    {
        if (record.Dice.RiddleFor(userId) is not { } riddle)
        {
            return false;
        }

        await reply(riddle.RuleId is null
            ? $":jigsaw: Answer your riddle first: _{riddle.Question}_\nJust type your answer in the thread."
            : $":question: Reply to this first: _{riddle.Question}_\nJust type your reply in the thread.");
        return true;
    }
}

using System.Text.Json.Nodes;
using ChessSlackBetter.Chess;

namespace ChessSlackBetter.Bot.Games;

public static class ActionIds
{
    public const string Accept = "chess_accept";
    public const string Decline = "chess_decline";
    public const string Resign = "chess_resign";
    public const string OfferDraw = "chess_offer_draw";
    public const string AcceptDraw = "chess_accept_draw";
    public const string DeclineDraw = "chess_decline_draw";
    public const string Roll = "chess_roll";
    public const string CancelRoll = "chess_cancel_roll";
    public const string Veto = "chess_veto";
}

public sealed record SlackContent(string Text, JsonArray Blocks);

/// <summary>Builds the Block Kit payloads the bot posts.</summary>
public static class Messages
{
    public const string HelpText = """
        *ChessBetterBot* — play chess in a Slack thread.

        *Start a game*
        • `@ChessBetterBot challenge @someone` — random colours
        • `@ChessBetterBot challenge @someone white` (or `black`) — pick your colour
        The challenge gets its own thread; your opponent accepts with the button or `@ChessBetterBot accept`.

        *In the game thread*
        • Make a move: `@ChessBetterBot e2e4`, `@ChessBetterBot Nf3`, or just type `e4` / `Nf3` / `O-O`
          Long notation works for everything: castle with `e1g1`, promote with `c7c8q` (q, r, b or n)
        • `@ChessBetterBot resign` · `@ChessBetterBot draw` (offer or accept) · `@ChessBetterBot decline draw`
        • `@ChessBetterBot abort` — cancel before both players have moved
        • `@ChessBetterBot board` — show the board again · `@ChessBetterBot pgn` — export / analyse

        *ChessDiceRoll* :game_die:
        • `@ChessBetterBot diceroll @someone` — normal chess, plus gambles
        • On your turn, instead of moving, describe a gamble with a reward *and* a penalty:
          `@ChessBetterBot roll turn my e4 pawn into a bishop, if I fail I lose the pawn`
        • The bot works out the fair d20 roll; you then press *Roll* (or say `yes`) or *Cancel* (`no`)
        • Rolling uses your turn. 3 rolls per player per game; cancelled rolls don't count
        • Your opponent can veto one gamble per game. Dice can't touch kings or deliver checkmate

        *Anywhere*
        • `@ChessBetterBot games` — your open games · `@ChessBetterBot stats [@someone]`
        • `@ChessBetterBot leaderboard` · `@ChessBetterBot leaderboard dice`
        • `@ChessBetterBot theme` · `@ChessBetterBot pieces` — pick your board colours and piece set, shown when it's your turn
        """;

    public static SlackContent ChallengeCard(string challengerId, string opponentId, string whiteId, GameMode mode = GameMode.Standard)
    {
        var colour = challengerId == whiteId ? "white" : "black";
        var dice = mode == GameMode.DiceRoll;
        var text = dice
            ? $"<@{challengerId}> has challenged <@{opponentId}> to a game of *ChessDiceRoll*!"
            : $"<@{challengerId}> has challenged <@{opponentId}> to a game of chess!";
        var blocks = new JsonArray
        {
            Section($"{(dice ? ":game_die:" : ":chess_pawn:")} {text}\n<@{challengerId}> will play *{colour}*."),
        };
        if (dice)
        {
            blocks.Add(Context("Normal chess, except on your turn you can gamble on a d20 instead of moving. " +
                               $"{DiceLimitsText}. `@ChessBetterBot help` for the rules."));
        }

        blocks.Add(Actions(
            Button("Accept", ActionIds.Accept, style: "primary"),
            Button("Decline", ActionIds.Decline, style: "danger")));
        blocks.Add(Context($"<@{opponentId}>, you can also reply in the thread with `@ChessBetterBot accept` or `@ChessBetterBot decline`."));
        return new SlackContent(text, blocks);
    }

    private static string DiceLimitsText =>
        $"{Dice.DiceLimits.RollsPerPlayer} rolls and {Dice.DiceLimits.VetoesPerPlayer} veto per player";

    /// <summary>The top-level card once the challenge has been answered: in progress, declined or finished.</summary>
    public static SlackContent StatusCard(GameRecord record, Game? game)
    {
        var players = $"<@{record.WhiteId}> (white) vs <@{record.BlackId}> (black)";
        if (record.Mode == GameMode.DiceRoll)
        {
            players = $":game_die: *ChessDiceRoll* · {players}";
        }

        var text = record.State switch
        {
            GameState.Active => $":chess_pawn: {players} — game in progress. Follow along in the thread.",
            GameState.Declined when record.Termination == Termination.Abandoned =>
                $":no_entry_sign: <@{record.ChallengerId}> withdrew the challenge to <@{record.OpponentId}>.",
            GameState.Declined => $":no_entry_sign: <@{record.OpponentId}> declined <@{record.ChallengerId}>'s challenge.",
            GameState.Finished when game is not null =>
                $":checkered_flag: {players} — *{game.ResultString}* · {DescribeResult(record, game)} after {MoveCountText(game)}.",
            _ => $":chess_pawn: {players}",
        };
        return new SlackContent(text, [Section(text)]);
    }

    /// <param name="style">The board preferences of the player to move.</param>
    public static SlackContent Board(GameRecord record, Game game, string? publicBaseUrl, Users.UserPreferences? style = null)
    {
        var position = game.Current;
        var toMove = record.PlayerFor(position.SideToMove);
        var colour = position.SideToMove == PieceColor.White ? "white" : "black";
        var header = $"*Move {position.FullmoveNumber}* · <@{toMove}>, it's your turn ({colour}).";
        if (position.IsInCheck())
        {
            header += " :warning: *Check!*";
        }

        if (LastMoveText(game) is { } last)
        {
            header += $"\nLast move: `{last}`";
        }

        var blocks = new JsonArray { Section(header) };
        blocks.Add(BoardBlock(game, position.SideToMove, publicBaseUrl, style));
        blocks.Add(Actions(
            Button("Offer draw", ActionIds.OfferDraw),
            Button("Resign", ActionIds.Resign, style: "danger", confirm: ("Resign?", "Are you sure you want to resign this game?", "Resign"))));
        if (record.Mode == GameMode.DiceRoll)
        {
            var dice = record.Dice;
            blocks.Add(Context(
                $":game_die: Rolls left: <@{record.WhiteId}> {dice.RollsLeft(record.WhiteId)} · <@{record.BlackId}> {dice.RollsLeft(record.BlackId)}" +
                $"  ·  Vetoes left: <@{record.WhiteId}> {dice.VetoesLeft(record.WhiteId)} · <@{record.BlackId}> {dice.VetoesLeft(record.BlackId)}\n" +
                "Move, or gamble: `@ChessBetterBot roll <what you want>, if I fail <penalty>`"));
        }
        else
        {
            blocks.Add(Context("Reply with a move like `e4`, `Nf3`, `O-O` or `e7e8q`. `@ChessBetterBot help` for more."));
        }

        return new SlackContent($"<@{toMove}>, it's your move ({colour}).", blocks);
    }

    public static SlackContent DiceProposal(GameRecord record, Dice.DiceProposal proposal)
    {
        var opponent = record.OpponentOf(proposal.RequesterId);
        var vetoes = record.Dice.VetoesLeft(opponent);
        var odds = $"Roll *{proposal.Target}+* on a d20 ({Dice.DiceRules.SuccessPercent(proposal.Target)}% chance)";
        var body =
            $":game_die: <@{proposal.RequesterId}> proposes a gamble:\n" +
            $">{proposal.Request}\n" +
            $":white_check_mark: *Success:* {proposal.SuccessSummary}\n" +
            $":x: *Failure:* {proposal.FailureSummary}\n" +
            $"{odds}. _{proposal.Reasoning}_";

        var buttons = new List<JsonObject>
        {
            Button(":game_die: Roll", ActionIds.Roll, style: "primary"),
            Button("Cancel", ActionIds.CancelRoll),
        };
        if (vetoes > 0)
        {
            buttons.Add(Button("Veto", ActionIds.Veto, style: "danger"));
        }

        var footer = $"<@{proposal.RequesterId}>: *Roll* (or `yes`) uses your turn and 1 of your {record.Dice.RollsLeft(proposal.RequesterId)} remaining rolls; *Cancel* (or `no`) is free.";
        footer += vetoes > 0
            ? $" <@{opponent}> can veto this ({vetoes} veto left)."
            : $" <@{opponent}> has no vetoes left.";

        return new SlackContent(
            $"<@{proposal.RequesterId}> proposes a gamble. {odds.Replace("*", "")}.",
            [Section(body), Actions(buttons.ToArray()), Context(footer)]);
    }

    /// <summary>Replaces the proposal once it's rolled, cancelled or vetoed, so its buttons go away.</summary>
    public static SlackContent DiceProposalClosed(Dice.DiceProposal proposal, string outcome)
    {
        var text = $":game_die: <@{proposal.RequesterId}>'s gamble: _{proposal.Request}_\n{outcome}";
        return new SlackContent(text, [Section(text)]);
    }

    public static string DiceRollOutcome(Dice.DiceProposal proposal, int roll, bool success) => success
        ? $":tada: Rolled *{roll}* (needed {proposal.Target}+). *Success!* {proposal.SuccessSummary}."
        : $":skull: Rolled *{roll}* (needed {proposal.Target}+). *Failed.* {proposal.FailureSummary}.";

    public static SlackContent GameOver(GameRecord record, Game game, string pgn, string analysisUrl, string? publicBaseUrl)
    {
        var description = DescribeResult(record, game);
        var text = $":checkered_flag: {description} *{game.ResultString}*";
        var blocks = new JsonArray
        {
            Section($"{text}\n<@{record.WhiteId}> (white) vs <@{record.BlackId}> (black) · {MoveCountText(game)}"),
        };
        if (game.Moves.Count > 0)
        {
            blocks.Add(BoardBlock(game, PieceColor.White, publicBaseUrl));
            foreach (var section in CodeSections(pgn))
            {
                blocks.Add(section);
            }

            blocks.Add(Actions(LinkButton(":mag: Analyze on lichess", analysisUrl)));
        }

        return new SlackContent($"{description} {game.ResultString}", blocks);
    }

    public static SlackContent DrawOffer(string offeredBy, string offeredTo)
    {
        var text = $":handshake: <@{offeredBy}> offers a draw. <@{offeredTo}>, do you accept?";
        return new SlackContent(text,
        [
            Section(text),
            Actions(
                Button("Accept draw", ActionIds.AcceptDraw, style: "primary"),
                Button("Decline", ActionIds.DeclineDraw)),
        ]);
    }

    public static SlackContent Pgn(string pgn, string analysisUrl)
    {
        var blocks = new JsonArray();
        foreach (var section in CodeSections(pgn))
        {
            blocks.Add(section);
        }

        blocks.Add(Actions(LinkButton(":mag: Analyze on lichess", analysisUrl)));
        return new SlackContent("Game PGN", blocks);
    }

    public static string DescribeResult(GameRecord record, Game game)
    {
        var winner = game.Outcome switch
        {
            GameOutcome.WhiteWins => record.WhiteId,
            GameOutcome.BlackWins => record.BlackId,
            _ => null,
        };
        var loser = winner is null ? null : record.OpponentOf(winner);

        return game.Termination switch
        {
            Termination.Checkmate => $"Checkmate! <@{winner}> wins.",
            Termination.Resignation => $"<@{loser}> resigned. <@{winner}> wins.",
            Termination.Stalemate => "Stalemate — it's a draw.",
            Termination.DrawAgreement => "Draw agreed.",
            Termination.ThreefoldRepetition => "Draw by threefold repetition.",
            Termination.FiftyMoveRule => "Draw by the fifty-move rule.",
            Termination.InsufficientMaterial => "Draw — insufficient material to mate.",
            Termination.Abandoned => "Game aborted.",
            _ => "Game over.",
        };
    }

    public static string BoardImageUrl(string publicBaseUrl, Game game, PieceColor perspective, Users.UserPreferences? style = null)
    {
        var url = $"{publicBaseUrl.TrimEnd('/')}/board.png?fen={Uri.EscapeDataString(game.Current.ToFen())}" +
                  $"&pov={(perspective == PieceColor.White ? 'w' : 'b')}";
        if (style?.Theme is { } theme)
        {
            url += $"&theme={Uri.EscapeDataString(theme)}";
        }

        if (style?.Pieces is { } pieces)
        {
            url += $"&pieces={Uri.EscapeDataString(pieces)}";
        }

        return game.LastMove switch
        {
            { Edits: { } edits } => $"{url}&hl={string.Join(',', edits.Select(e => Square.Name(e.Square)))}",
            { } last => $"{url}&last={last.Move.ToUci()}",
            _ => url,
        };
    }

    private static JsonNode BoardBlock(Game game, PieceColor perspective, string? publicBaseUrl, Users.UserPreferences? style = null)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return Section($"```{Rendering.BoardRenderer.RenderText(game.Current, perspective)}```");
        }

        return new JsonObject
        {
            ["type"] = "image",
            ["image_url"] = BoardImageUrl(publicBaseUrl, game, perspective, style),
            ["alt_text"] = $"Chess board: {game.Current.ToFen()}",
        };
    }

    private static string? LastMoveText(Game game)
    {
        if (game.LastMove is not { } last)
        {
            return null;
        }

        var index = game.Moves.Count - 1;
        var number = index / 2 + 1;
        return index % 2 == 0 ? $"{number}. {last.San}" : $"{number}... {last.San}";
    }

    private static string MoveCountText(Game game)
    {
        var moves = (game.Moves.Count + 1) / 2;
        return moves == 1 ? "1 move" : $"{moves} moves";
    }

    /// <summary>Splits text into code-formatted sections that fit Slack's 3000 character limit per block.</summary>
    private static IEnumerable<JsonObject> CodeSections(string text)
    {
        const int max = 2900;
        var remaining = text;
        while (remaining.Length > max)
        {
            var cut = remaining.LastIndexOf(' ', max);
            cut = cut <= 0 ? max : cut;
            yield return Section($"```{remaining[..cut]}```");
            remaining = remaining[cut..].TrimStart();
        }

        yield return Section($"```{remaining}```");
    }

    private static JsonObject Section(string mrkdwn) => new()
    {
        ["type"] = "section",
        ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = mrkdwn },
    };

    private static JsonObject Context(string mrkdwn) => new()
    {
        ["type"] = "context",
        ["elements"] = new JsonArray { new JsonObject { ["type"] = "mrkdwn", ["text"] = mrkdwn } },
    };

    private static JsonObject Actions(params JsonObject[] elements) => new()
    {
        ["type"] = "actions",
        ["elements"] = new JsonArray(elements.Cast<JsonNode>().ToArray()),
    };

    private static JsonObject Button(string label, string actionId, string? style = null, (string Title, string Text, string Confirm)? confirm = null)
    {
        var button = new JsonObject
        {
            ["type"] = "button",
            ["text"] = PlainText(label),
            ["action_id"] = actionId,
        };
        if (style is not null) button["style"] = style;
        if (confirm is { } c)
        {
            button["confirm"] = new JsonObject
            {
                ["title"] = PlainText(c.Title),
                ["text"] = PlainText(c.Text),
                ["confirm"] = PlainText(c.Confirm),
                ["deny"] = PlainText("Keep playing"),
                ["style"] = "danger",
            };
        }

        return button;
    }

    private static JsonObject LinkButton(string label, string url) => new()
    {
        ["type"] = "button",
        ["text"] = PlainText(label),
        ["action_id"] = "chess_link",
        ["url"] = url,
    };

    private static JsonObject PlainText(string text) => new() { ["type"] = "plain_text", ["text"] = text, ["emoji"] = true };
}

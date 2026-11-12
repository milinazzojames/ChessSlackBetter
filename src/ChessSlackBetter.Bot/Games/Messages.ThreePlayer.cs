using System.Text.Json.Nodes;
using ChessSlackBetter.Chess;
using ChessSlackBetter.Chess.ThreePlayer;

namespace ChessSlackBetter.Bot.Games;

/// <summary>Block Kit payloads for three-player chess.</summary>
public static partial class Messages
{
    public const string ThreePlayerName = "Three-Player Chess";

    public const string ThreePlayerDiceName = "Three-Player ChessDiceRoll";

    private const string ThreePlayerIcon = ":small_red_triangle:";

    private const string ThreePlayerMoveHelp =
        "Reply with a move like `d4`, `Nc3`, `i7i5` or `O-O` (files a–l, ranks 1–12). `@ChessBetterBot help` for the rules.";

    public static SlackContent ThreePlayerChallengeCard(ThreePlayerRecord record)
    {
        var invited = record.Players.Where(p => p != record.ChallengerId).ToList();
        var text = $"<@{record.ChallengerId}> has challenged <@{invited[0]}> and <@{invited[1]}> to *{GameName(record)}*!";
        var waiting = invited.Where(p => !record.Accepted.Contains(p)).Select(p => $"<@{p}>");
        return new SlackContent(text,
        [
            Section($"{ThreePlayerIcon} {text}\n{Seats(record, null)}"),
            Context("A triangular board for three. Checkmate someone and they're out; the last player standing wins. " +
                    "Pawns promote on either opponent's back rank. " +
                    (record.WithDice
                        ? $"On your turn you can gamble on a d100 instead of moving: {DiceLimitsText}, so each gamble can be vetoed by either opponent. "
                        : "") +
                    "`@ChessBetterBot help` for the rules."),
            Actions(
                Button("Accept", ActionIds.Accept, style: "primary"),
                Button("Decline", ActionIds.Decline, style: "danger")),
            Context($"Waiting for {string.Join(" and ", waiting)} to accept. You can also reply in the thread with `@ChessBetterBot accept` or `@ChessBetterBot decline`."),
        ]);
    }

    public static SlackContent ThreePlayerStatusCard(ThreePlayerRecord record, TriGame? game)
    {
        var text = record.State switch
        {
            GameState.Active => $"{ThreePlayerIcon} *{GameName(record)}* · {Seats(record, game)} — game in progress. Follow along in the thread.",
            GameState.Declined when record.DeclinedBy == record.ChallengerId =>
                $":no_entry_sign: <@{record.ChallengerId}> withdrew their {GameName(record)} challenge.",
            GameState.Declined => $":no_entry_sign: <@{record.DeclinedBy}> declined <@{record.ChallengerId}>'s {GameName(record)} challenge.",
            GameState.Finished when game is not null =>
                $":checkered_flag: *{GameName(record)}* · {DescribeThreePlayerResult(record, game)} {Podium(record, game)}",
            _ => $"{ThreePlayerIcon} *{GameName(record)}* · {Seats(record, game)}",
        };
        return new SlackContent(text, [Section(text)]);
    }

    public static SlackContent ThreePlayerBoard(ThreePlayerRecord record, TriGame game, string? publicBaseUrl)
    {
        var position = game.Current;
        var toMove = record.PlayerFor(position.SideToMove);
        var header = $"*Round {RoundNumber(game)}* · <@{toMove}>, it's your turn ({position.SideToMove.Name()}).";
        foreach (var color in TriColors.All.Where(position.IsInCheck))
        {
            header += $"\n:warning: <@{record.PlayerFor(color)}> is in *check!*";
        }

        if (game.LastMove is { } last)
        {
            header += $"\nLast move: <@{record.PlayerFor(last.Player)}> played `{last.San}`";
        }

        return new SlackContent($"<@{toMove}>, it's your move ({position.SideToMove.Name()}).",
        [
            Section(header),
            ThreePlayerBoardBlock(game, position.SideToMove, publicBaseUrl),
            Context(Seats(record, game)),
            Actions(
                Button("Offer draw", ActionIds.OfferDraw),
                Button("Resign", ActionIds.Resign, style: "danger", confirm: ("Resign?", "You'll be out of the game and your pieces leave the board.", "Resign"))),
            Context(record.WithDice ? ThreePlayerDiceHelp(record, game) : ThreePlayerMoveHelp),
        ]);
    }

    /// <summary>Announces a player going out: checkmated or resigned.</summary>
    public static string ThreePlayerElimination(ThreePlayerRecord record, TriElimination elimination) =>
        elimination.Reason == Termination.Checkmate
            ? $":skull: *Checkmate!* <@{record.PlayerFor(elimination.Player)}> ({elimination.Player.Name()}) is out" +
              (elimination.By is { } by ? $", mated by <@{record.PlayerFor(by)}>." : ".") + " Their pieces leave the board."
            : $":white_flag: <@{record.PlayerFor(elimination.Player)}> ({elimination.Player.Name()}) resigned and is out. Their pieces leave the board.";

    public static SlackContent ThreePlayerDrawOffer(ThreePlayerRecord record, string offeredBy, IEnumerable<string> stillToAgree)
    {
        var waiting = string.Join(" and ", stillToAgree.Select(u => $"<@{u}>"));
        var text = $":handshake: <@{offeredBy}> wants a draw. {waiting}, do you agree? Everyone still playing has to.";
        return new SlackContent(text,
        [
            Section(text),
            Actions(
                Button("Agree to draw", ActionIds.AcceptDraw, style: "primary"),
                Button("Decline", ActionIds.DeclineDraw)),
        ]);
    }

    public static SlackContent ThreePlayerGameOver(ThreePlayerRecord record, TriGame game, string? publicBaseUrl)
    {
        var description = DescribeThreePlayerResult(record, game);
        var text = $":checkered_flag: {description}";
        var blocks = new JsonArray { Section($"{text}\n{Podium(record, game)}") };
        if (game.Moves.Count > 0)
        {
            blocks.Add(ThreePlayerBoardBlock(game, TriColor.White, publicBaseUrl));
            foreach (var section in CodeSections(game.MovesText()))
            {
                blocks.Add(section);
            }
        }

        return new SlackContent(description, blocks);
    }

    public static SlackContent ThreePlayerMoves(TriGame game)
    {
        var blocks = new JsonArray();
        foreach (var section in CodeSections(game.MovesText()))
        {
            blocks.Add(section);
        }

        return new SlackContent("Move list", blocks);
    }

    public static string DescribeThreePlayerResult(ThreePlayerRecord record, TriGame game)
    {
        var first = game.Ranking.FirstOrDefault() ?? [];
        var winners = string.Join(" and ", first.Select(c => $"<@{record.PlayerFor(c)}>"));
        return game.Termination switch
        {
            Termination.Abandoned => "Game aborted.",
            Termination.DrawAgreement => $"Draw agreed between {winners}.",
            Termination.Stalemate => $"Stalemate — {winners} share first place.",
            Termination.InsufficientMaterial => $"Only kings are left — {winners} share first place.",
            _ when first.Count == 1 => $"<@{record.PlayerFor(first[0])}> is the last player standing and wins!",
            _ => "Game over.",
        };
    }

    public static string ThreePlayerBoardImageUrl(string publicBaseUrl, TriGame game, TriColor perspective)
    {
        var url = $"{publicBaseUrl.TrimEnd('/')}/board3.png?pos={Uri.EscapeDataString(game.Current.ToCode())}" +
                  $"&pov={TriColors.Letter(perspective)}";
        return game.LastMove switch
        {
            { Edits: { } edits } => $"{url}&hl={string.Join(',', edits.Select(e => TriSquare.Name(e.Square)))}",
            { } last => $"{url}&hl={TriSquare.Name(last.Move.From)},{TriSquare.Name(last.Move.To)}",
            _ => url,
        };
    }

    private static JsonNode ThreePlayerBoardBlock(TriGame game, TriColor perspective, string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return Section($"```{Rendering.BoardRenderer.RenderThreePlayerText(game.Current)}```");
        }

        return new JsonObject
        {
            ["type"] = "image",
            ["image_url"] = ThreePlayerBoardImageUrl(publicBaseUrl, game, perspective),
            ["alt_text"] = "Three-player chess board",
        };
    }

    public static SlackContent ThreePlayerDiceProposal(ThreePlayerRecord record, TriGame game, Dice.DiceProposal proposal)
    {
        var odds = $"Roll *{proposal.Target}+* on a {Dice.DiceRules.DieName} ({Dice.DiceRules.SuccessPercent(proposal.Target)}% chance)";
        var body =
            $":game_die: <@{proposal.RequesterId}> proposes a gamble:\n" +
            $">{proposal.Request}\n" +
            $":white_check_mark: *Success:* {proposal.SuccessSummary}\n" +
            $":x: *Failure:* {proposal.FailureSummary}\n" +
            $"{odds}. _{proposal.Reasoning}_";

        var opponents = game.Current.AliveColors.Select(record.PlayerFor).Where(p => p != proposal.RequesterId).ToList();
        var canVeto = opponents.Where(p => record.Dice.VetoesLeft(p) > 0).ToList();
        var buttons = new List<JsonObject>
        {
            Button(":game_die: Roll", ActionIds.Roll, style: "primary"),
            Button("Cancel", ActionIds.CancelRoll),
        };
        if (canVeto.Count > 0)
        {
            buttons.Add(Button("Veto", ActionIds.Veto, style: "danger"));
        }

        var footer = $"<@{proposal.RequesterId}>: *Roll* (or `yes`) uses your turn and 1 of your {record.Dice.RollsLeft(proposal.RequesterId)} remaining rolls; *Cancel* (or `no`) is free.";
        footer += canVeto.Count > 0
            ? $" {string.Join(" or ", canVeto.Select(p => $"<@{p}>"))} can veto this."
            : " Nobody has a veto left.";

        return new SlackContent(
            $"<@{proposal.RequesterId}> proposes a gamble. {odds.Replace("*", "")}.",
            [Section(body), Actions(buttons.ToArray()), Context(footer)]);
    }

    private static string GameName(ThreePlayerRecord record) => record.WithDice ? ThreePlayerDiceName : ThreePlayerName;

    private static string ThreePlayerDiceHelp(ThreePlayerRecord record, TriGame game)
    {
        var alive = game.Current.AliveColors.Select(record.PlayerFor).ToList();
        return $":game_die: Rolls left: {string.Join(" · ", alive.Select(p => $"<@{p}> {record.Dice.RollsLeft(p)}"))}" +
               $"  ·  Vetoes left: {string.Join(" · ", alive.Select(p => $"<@{p}> {record.Dice.VetoesLeft(p)}"))}\n" +
               "Move (files a–l, ranks 1–12), or gamble: `@ChessBetterBot roll <what you want>, if I fail <penalty>`";
    }

    /// <summary>Who plays which colour; anyone already out is struck through.</summary>
    private static string Seats(ThreePlayerRecord record, TriGame? game) => string.Join(" · ", TriColors.All.Select(color =>
    {
        var seat = $"{SeatIcon(color)} {color} <@{record.PlayerFor(color)}>";
        return game?.IsEliminated(color) == true ? $"~{seat}~" : seat;
    }));

    private static string Podium(ThreePlayerRecord record, TriGame game)
    {
        string[] medals = [":first_place_medal:", ":second_place_medal:", ":third_place_medal:"];
        var place = 0;
        var parts = new List<string>();
        foreach (var group in game.Ranking)
        {
            parts.Add($"{medals[place]} {string.Join(" ", group.Select(c => $"<@{record.PlayerFor(c)}>"))}");
            place += group.Count;
        }

        return string.Join("  ", parts);
    }

    private static string SeatIcon(TriColor color) => color switch
    {
        TriColor.White => ":white_circle:",
        TriColor.Black => ":black_circle:",
        _ => ":red_circle:",
    };

    /// <summary>A round is one turn each; it starts again whenever the turn comes back round to an earlier seat.</summary>
    private static int RoundNumber(TriGame game)
    {
        var round = 1;
        TriColor? previous = null;
        foreach (var turn in game.Moves)
        {
            if (previous is not null && turn.Player <= previous)
            {
                round++;
            }

            previous = turn.Player;
        }

        return previous is not null && game.Current.SideToMove <= previous ? round + 1 : round;
    }
}

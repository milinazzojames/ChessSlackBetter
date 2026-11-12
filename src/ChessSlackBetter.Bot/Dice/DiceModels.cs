namespace ChessSlackBetter.Bot.Dice;

public static class DiceLimits
{
    /// <summary>Accepted (rolled) gambles each player gets per game. Cancelled or vetoed ones don't count.</summary>
    public const int RollsPerPlayer = 5;

    public const int VetoesPerPlayer = 1;
}

/// <summary>One change to the board, as described by the referee. Unused fields are empty strings.</summary>
/// <param name="Type">transform, remove, add or move.</param>
/// <param name="Square">The square affected (the source square for a move).</param>
/// <param name="ToSquare">Destination for a move.</param>
/// <param name="Piece">New piece type for transform/add: pawn, knight, bishop, rook or queen.</param>
/// <param name="Color">Colour for add (or to change sides in a transform): white or black.</param>
public sealed record DiceEffect(string Type, string Square, string ToSquare, string Piece, string Color);

/// <summary>One way an outcome can play out. Effects may be empty when nothing changes.</summary>
public sealed record DiceOption(IReadOnlyList<DiceEffect> Effects, string Summary, IReadOnlyList<DiceRuleSpec>? Rules = null);

/// <summary>When a passive rule fires, relative to the player who rolled the gamble.</summary>
public static class RuleTriggers
{
    public const string OwnTurn = "own_turn";
    public const string OpponentTurn = "opponent_turn";
    public const string EveryTurn = "every_turn";

    public static readonly string[] All = [OwnTurn, OpponentTurn, EveryTurn];
}

/// <summary>
/// A passive rule as the referee describes it: effects applied at the end of matching turns from now on.
/// </summary>
/// <param name="Trigger">One of <see cref="RuleTriggers"/>.</param>
/// <param name="Every">Fires on every Nth matching turn (1 = each one).</param>
/// <param name="Times">How many times it fires before expiring; 0 means for the rest of the game.</param>
/// <param name="Effects">Applied in order each time; squares may be random selectors (see <see cref="DiceRules"/>).
/// For a riddle rule these are what happens on a wrong answer.</param>
/// <param name="Challenge">Empty for a plain rule, or <see cref="RuleChallenges.Riddle"/>: before each matching turn the
/// player about to move has to answer a riddle first.</param>
/// <param name="CorrectEffects">For a riddle rule, what happens on a right answer.</param>
/// <param name="Theme">For a riddle rule, the kind of riddle the requester asked for (may be empty).</param>
public sealed record DiceRuleSpec(
    string Trigger,
    int Every,
    int Times,
    IReadOnlyList<DiceEffect> Effects,
    string Summary,
    string Challenge = "",
    IReadOnlyList<DiceEffect>? CorrectEffects = null,
    string Theme = "",
    string Custom = "",
    string InitialMemory = "",
    double CustomValue = 0)
{
    public bool IsRiddle => RuleChallenges.IsRiddle(Challenge);

    /// <summary>
    /// A custom rule is run by the referee itself each time it fires: <see cref="Custom"/> is the rule in plain
    /// English, and the referee decides the effects from the board and the rule's memory. For anything the fixed
    /// effects can't express.
    /// </summary>
    public bool IsCustom => !string.IsNullOrWhiteSpace(Custom);
}

/// <summary>What a custom rule's referee is told when the rule fires.</summary>
/// <param name="Rule">The rule in plain English.</param>
/// <param name="Owner">The colour of the player whose gamble created the rule.</param>
/// <param name="Memory">Whatever the rule wrote down last time (free-form, usually JSON).</param>
/// <param name="Event">What just happened: the end of a turn, or a player's answer to the rule's question.</param>
/// <param name="RandomNumbers">Fresh random numbers from 1 to 100, so the referee never has to invent randomness.</param>
/// <param name="Damage">Damage on the board's pieces, by square.</param>
public sealed record CustomRuleCall(
    ChessSlackBetter.Chess.Position Position,
    string Rule,
    ChessSlackBetter.Chess.PieceColor Owner,
    string Memory,
    string Event,
    IReadOnlyList<int> RandomNumbers,
    IReadOnlyDictionary<string, int> Damage);

/// <summary>The referee's ruling for one firing of a custom rule. Unused fields are empty.</summary>
/// <param name="Effects">Board effects, applied after the turn like any rule's.</param>
/// <param name="Memory">The rule's memory for next time.</param>
/// <param name="Message">What to tell the players about this firing.</param>
/// <param name="Status">A short line shown under the board while the rule is in play (e.g. scores or health).</param>
/// <param name="AskColor">white or black to ask that player <paramref name="Question"/>; they must reply before moving.</param>
/// <param name="Expire">True when the rule has run its course.</param>
public sealed record CustomRuleResult(
    IReadOnlyList<DiceEffect> Effects,
    string Memory,
    string Message,
    string Status,
    string AskColor,
    string Question,
    bool Expire);

public static class RuleChallenges
{
    public const string None = "";

    public const string Riddle = "riddle";

    public static bool IsRiddle(string? challenge) => string.Equals(challenge, Riddle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A riddle waiting for <see cref="SolverId"/> to answer before they can move.</summary>
public sealed class PendingRiddle
{
    public required string SolverId { get; init; }

    public required string Question { get; init; }

    /// <summary>The riddle's answer; empty for a custom rule's question, which the referee judges.</summary>
    public required string Answer { get; init; }

    /// <summary>Set when a custom rule asked this; the reply goes back to that rule.</summary>
    public string? RuleId { get; init; }

    /// <summary>
    /// Slack ts of the message that asked it; null until it's posted. Only messages sent after this count as
    /// answers: anything typed earlier (e.g. while the referee was still writing the question) is ignored.
    /// </summary>
    public string? AskedTs { get; set; }

    /// <summary>Whether a message with Slack ts <paramref name="messageTs"/> was sent after the question was posted.</summary>
    public bool IsAnsweredBy(string messageTs) =>
        AskedTs is null || // asked before timestamps were recorded
        decimal.TryParse(messageTs, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var sent) &&
        decimal.TryParse(AskedTs, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var asked) &&
        sent > asked;

    /// <summary>Other answers that also count as right, e.g. a plural or synonym.</summary>
    public List<string> AlsoAccepted { get; init; } = [];

    /// <summary>The rule's effects with its target colours already pointed at the solver.</summary>
    public List<DiceEffect> CorrectEffects { get; init; } = [];

    public List<DiceEffect> WrongEffects { get; init; } = [];

    public required string Summary { get; init; }
}

/// <summary>A riddle as written by the referee.</summary>
public sealed record Riddle(string Question, string Answer, IReadOnlyList<string> AlsoAccepted);

/// <summary>A passive rule in play. Stored as JSON with the game's dice state.</summary>
public sealed class PassiveRule
{
    /// <summary>The player whose gamble created the rule; triggers are relative to them.</summary>
    public required string OwnerId { get; init; }

    public required string Trigger { get; init; }

    public int Every { get; init; } = 1;

    /// <summary>Firings left, or null for the rest of the game.</summary>
    public int? TimesLeft { get; set; }

    /// <summary>Matching turns seen so far, for <see cref="Every"/>.</summary>
    public int TurnsSeen { get; set; }

    public List<DiceEffect> Effects { get; init; } = [];

    public required string Summary { get; init; }

    /// <summary>Empty, or <see cref="RuleChallenges.Riddle"/> (then <see cref="Effects"/> are for a wrong answer).</summary>
    public string Challenge { get; init; } = "";

    public List<DiceEffect> CorrectEffects { get; init; } = [];

    public string Theme { get; init; } = "";

    public bool IsRiddle => RuleChallenges.IsRiddle(Challenge);

    /// <summary>Identifies the rule, so a question it asked can find it again.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>For a custom rule, the rule in plain English (see <see cref="DiceRuleSpec.IsCustom"/>).</summary>
    public string Custom { get; init; } = "";

    /// <summary>A custom rule's memory between firings.</summary>
    public string Memory { get; set; } = "";

    /// <summary>A custom rule's latest status line, shown under the board.</summary>
    public string Status { get; set; } = "";

    public bool IsCustom => !string.IsNullOrWhiteSpace(Custom);

    /// <summary>
    /// Whether the rule applies to a turn by <paramref name="playerId"/>: the player who just moved for a plain
    /// rule, or the player about to move for a riddle.
    /// </summary>
    public bool Matches(string playerId) => Matches(Trigger, OwnerId, playerId);

    public static bool Matches(string trigger, string ownerId, string playerId) => trigger switch
    {
        RuleTriggers.OwnTurn => playerId == ownerId,
        RuleTriggers.OpponentTurn => playerId != ownerId,
        _ => true,
    };

    public static PassiveRule From(DiceRuleSpec spec, string ownerId) => new()
    {
        OwnerId = ownerId,
        Trigger = RuleTriggers.All.Contains(spec.Trigger) ? spec.Trigger : RuleTriggers.OwnTurn,
        Every = Math.Max(1, spec.Every),
        TimesLeft = spec.Times > 0 ? spec.Times : null,
        Effects = spec.Effects.ToList(),
        Summary = spec.Summary,
        Challenge = spec.IsRiddle ? RuleChallenges.Riddle : RuleChallenges.None,
        CorrectEffects = spec.CorrectEffects?.ToList() ?? [],
        Theme = spec.Theme ?? "",
        Custom = spec.Custom ?? "",
        Memory = spec.InitialMemory ?? "",
    };
}

/// <summary>
/// The referee's reading of a gamble request. Each outcome has one or more options; when there are several
/// (a random request), one is picked uniformly at random when the dice are rolled.
/// </summary>
public sealed record DiceRuling(
    bool Accepted,
    string Message,
    IReadOnlyList<DiceOption> SuccessOptions,
    IReadOnlyList<DiceOption> FailureOptions,
    string SuccessSummary,
    string FailureSummary,
    double SuccessPositionalBonus,
    double FailurePositionalBonus,
    string Reasoning);

/// <summary>A gamble waiting for the requester to roll or cancel (or the opponent to veto).</summary>
public sealed class DiceProposal
{
    public required string RequesterId { get; init; }

    public required string Request { get; init; }

    /// <summary>Possible results of winning; one is picked at random when rolled.</summary>
    public List<DiceOutcome> SuccessOutcomes { get; init; } = [];

    public List<DiceOutcome> FailureOutcomes { get; init; } = [];

    public required string SuccessSummary { get; init; }

    public required string FailureSummary { get; init; }

    /// <summary>Minimum d100 roll needed to succeed.</summary>
    public required int Target { get; init; }

    public required string Reasoning { get; init; }

    /// <summary>The Slack message showing the proposal, so it can be updated with the outcome.</summary>
    public string? MessageTs { get; set; }
}

/// <summary>A checked, ready-to-apply result. Square edits in text form, e.g. "e4=B"; empty means nothing changes.</summary>
/// <param name="Resigns">The requester resigns and the game ends (the board edits and rules don't matter then).</param>
public sealed record DiceOutcome(List<string> Edits, string Summary, List<DiceRuleSpec>? Rules = null, bool Resigns = false)
{
    public bool ChangesNothing => !Resigns && Edits.Count == 0 && (Rules is null || Rules.Count == 0);
}

public sealed record DiceRollResult(string UserId, string Request, int Target, int Roll, bool Success, string Summary);

/// <summary>Everything a dice game tracks on top of the moves. Stored as JSON on the game.</summary>
public sealed class DiceState
{
    public Dictionary<string, int> RollsUsed { get; set; } = new();

    public List<string> VetoesUsed { get; set; } = [];

    public DiceProposal? Pending { get; set; }

    public List<DiceRollResult> Log { get; set; } = [];

    /// <summary>Passive rules won (or lost) from gambles, still in play.</summary>
    public List<PassiveRule> Rules { get; set; } = [];

    /// <summary>Riddles waiting to be answered, oldest first. The solver can't move or gamble until theirs are done.</summary>
    public List<PendingRiddle> Riddles { get; set; } = [];

    /// <summary>Answers to riddles already asked this game, so the referee doesn't repeat itself.</summary>
    public List<string> RiddleAnswers { get; set; } = [];

    /// <summary>Damage taken by pieces still on the board (square name to points), from damage effects.</summary>
    public Dictionary<string, int> Damage { get; set; } = new();

    public PendingRiddle? RiddleFor(string userId) => Riddles.FirstOrDefault(r => r.SolverId == userId);

    public int RollsLeft(string userId) => DiceLimits.RollsPerPlayer - RollsUsed.GetValueOrDefault(userId);

    public int VetoesLeft(string userId) => DiceLimits.VetoesPerPlayer - VetoesUsed.Count(u => u == userId);
}

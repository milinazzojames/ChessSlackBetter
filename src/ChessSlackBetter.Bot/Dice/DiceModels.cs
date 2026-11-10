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
/// <param name="Effects">Applied in order each time; squares may be random selectors (see <see cref="DiceRules"/>).</param>
public sealed record DiceRuleSpec(string Trigger, int Every, int Times, IReadOnlyList<DiceEffect> Effects, string Summary);

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

    public bool Matches(string moverId) => Trigger switch
    {
        RuleTriggers.OwnTurn => moverId == OwnerId,
        RuleTriggers.OpponentTurn => moverId != OwnerId,
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

    /// <summary>Minimum d20 roll needed to succeed.</summary>
    public required int Target { get; init; }

    public required string Reasoning { get; init; }

    /// <summary>The Slack message showing the proposal, so it can be updated with the outcome.</summary>
    public string? MessageTs { get; set; }
}

/// <summary>A checked, ready-to-apply result. Square edits in text form, e.g. "e4=B"; empty means nothing changes.</summary>
public sealed record DiceOutcome(List<string> Edits, string Summary, List<DiceRuleSpec>? Rules = null)
{
    public bool ChangesNothing => Edits.Count == 0 && (Rules is null || Rules.Count == 0);
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

    public int RollsLeft(string userId) => DiceLimits.RollsPerPlayer - RollsUsed.GetValueOrDefault(userId);

    public int VetoesLeft(string userId) => DiceLimits.VetoesPerPlayer - VetoesUsed.Count(u => u == userId);
}

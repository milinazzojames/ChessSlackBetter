using System.Text;
using ChessSlackBetter.Bot.Dice;

namespace ChessSlackBetter.Bot.Games;

public static partial class Messages
{
    public static string RiddleQuestion(PendingRiddle riddle) =>
        $":jigsaw: <@{riddle.SolverId}>, riddle time! Answer before you move.\n>{riddle.Question}\n" +
        $"_Type your answer in the thread (one word). Rule: {riddle.Summary}_";

    /// <summary>
    /// Whether <paramref name="answer"/> matches the riddle's answer, ignoring case, punctuation, a leading
    /// "a"/"an"/"the" and a trailing plural "s".
    /// </summary>
    public static bool IsRiddleAnswer(string answer, PendingRiddle riddle)
    {
        var given = NormalizeAnswer(answer);
        return given.Length > 0 && riddle.AlsoAccepted.Prepend(riddle.Answer).Select(NormalizeAnswer).Any(a => a == given);
    }

    private static string NormalizeAnswer(string text)
    {
        var words = text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] is "a" or "an" or "the")
        {
            words.RemoveAt(0);
        }

        var sb = new StringBuilder();
        foreach (var c in string.Concat(words).Where(char.IsLetterOrDigit))
        {
            sb.Append(c);
        }

        var result = sb.ToString();
        return result.Length > 3 && result.EndsWith('s') ? result[..^1] : result;
    }
}

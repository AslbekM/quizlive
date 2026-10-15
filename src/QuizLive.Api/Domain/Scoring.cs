namespace QuizLive.Domain;

/// <summary>
/// How an answer turns into points. Kept as a pure function with no clock and
/// no database so the rules can be tested directly, and so the only time that
/// can ever reach it is the elapsed figure the server measured itself.
/// </summary>
public static class Scoring
{
    /// <summary>Points for a correct answer given instantly.</summary>
    public const int MaxPoints = 1000;

    /// <summary>Points for a correct answer given at the buzzer.</summary>
    public const int MinPointsForCorrect = 500;

    /// <summary>
    /// A correct answer scores between <see cref="MinPointsForCorrect"/> and
    /// <see cref="MaxPoints"/>, scaled linearly by how much of the time limit
    /// was left; a wrong one scores nothing.
    /// </summary>
    /// <remarks>
    /// Elapsed time is clamped to the limit rather than zeroed past it, so an
    /// answer let through by the grace window in
    /// <see cref="IsWithinTimeLimit"/> still scores the floor. Whether an
    /// answer counts at all is that method's decision, not this one's —
    /// otherwise the two rules disagree about the buzzer.
    /// </remarks>
    public static int Points(bool isCorrect, int elapsedMs, int timeLimitSeconds)
    {
        if (!isCorrect) return 0;
        if (elapsedMs < 0) elapsedMs = 0;

        var limitMs = timeLimitSeconds * 1000;
        if (limitMs <= 0) return MaxPoints;

        var used = Math.Clamp((double)elapsedMs / limitMs, 0, 1);
        var bonus = (MaxPoints - MinPointsForCorrect) * (1 - used);
        return MinPointsForCorrect + (int)Math.Round(bonus);
    }

    /// <summary>
    /// Whether an answer arrived in time. A small grace window absorbs network
    /// latency, so a player who tapped just before the buzzer isn't punished
    /// for their connection.
    /// </summary>
    public static bool IsWithinTimeLimit(int elapsedMs, int timeLimitSeconds, int graceMs = 750) =>
        elapsedMs <= timeLimitSeconds * 1000 + graceMs;
}

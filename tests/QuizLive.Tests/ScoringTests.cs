using QuizLive.Domain;
using Xunit;

namespace QuizLive.Tests;

/// <summary>
/// The point formula on its own. No clock, no database: just the arithmetic
/// everything else depends on.
/// </summary>
public class ScoringTests
{
    [Fact]
    public void WrongAnswer_ScoresNothing_HowEverFast()
    {
        Assert.Equal(0, Scoring.Points(isCorrect: false, elapsedMs: 0, timeLimitSeconds: 20));
    }

    [Fact]
    public void InstantCorrectAnswer_ScoresTheMaximum()
    {
        Assert.Equal(Scoring.MaxPoints, Scoring.Points(true, elapsedMs: 0, timeLimitSeconds: 20));
    }

    [Fact]
    public void CorrectAnswerAtTheBuzzer_ScoresTheFloor()
    {
        Assert.Equal(Scoring.MinPointsForCorrect, Scoring.Points(true, elapsedMs: 20_000, timeLimitSeconds: 20));
    }

    [Fact]
    public void FasterCorrectAnswers_AlwaysScoreMore()
    {
        var fast = Scoring.Points(true, 2_000, 20);
        var middling = Scoring.Points(true, 10_000, 20);
        var slow = Scoring.Points(true, 18_000, 20);

        Assert.True(fast > middling, $"{fast} should beat {middling}");
        Assert.True(middling > slow, $"{middling} should beat {slow}");
    }

    [Fact]
    public void HalfTheLimit_ScoresHalfTheSpeedBonus()
    {
        var expected = Scoring.MinPointsForCorrect + (Scoring.MaxPoints - Scoring.MinPointsForCorrect) / 2;
        Assert.Equal(expected, Scoring.Points(true, 10_000, 20));
    }

    /// <summary>
    /// The grace window and the formula have to agree about the buzzer. An
    /// answer accepted a few hundred ms late must still score the floor, not
    /// silently collapse to zero.
    /// </summary>
    [Fact]
    public void AnswerInsideTheGraceWindow_IsAcceptedAndStillScores()
    {
        const int justLate = 20_200;

        Assert.True(Scoring.IsWithinTimeLimit(justLate, 20));
        Assert.Equal(Scoring.MinPointsForCorrect, Scoring.Points(true, justLate, 20));
    }

    [Fact]
    public void AnswerWellPastTheLimit_IsRejected()
    {
        Assert.False(Scoring.IsWithinTimeLimit(25_000, 20));
    }

    [Fact]
    public void NegativeElapsedTime_IsTreatedAsInstant()
    {
        Assert.Equal(Scoring.MaxPoints, Scoring.Points(true, -500, 20));
    }
}

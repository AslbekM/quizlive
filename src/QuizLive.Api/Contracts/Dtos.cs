namespace QuizLive.Contracts;

/// <summary>What a player sees for the live question — never the correct index.</summary>
public record QuestionView(
    int QuestionId,
    int Index,
    int TotalQuestions,
    string Text,
    string[] Options,
    int TimeLimitSeconds,
    int MsRemaining);

public record ScoreRow(int PlayerId, string Name, int Score);

public record RevealView(
    int QuestionId,
    int CorrectIndex,
    int[] CountsPerOption,
    ScoreRow[] Leaderboard);

/// <summary>
/// Everything a client needs to render from scratch. Sent on join and on every
/// reconnect, so a reloaded phone lands exactly where the game actually is
/// instead of guessing from whatever it missed.
/// </summary>
public record StateSnapshot(
    string Code,
    string State,
    string QuizTitle,
    int PlayerCount,
    QuestionView? Question,
    RevealView? Reveal,
    ScoreRow[] Leaderboard,
    PlayerSelf? You,
    int AnswersIn);

public record PlayerSelf(
    int PlayerId,
    string Name,
    int Score,
    string ReconnectToken,
    int? AnsweredOptionIndex,
    /// <summary>
    /// Points banked for the question currently in play. Held here rather than
    /// only in the client's reply to its own submission, because that reply is
    /// gone after a refresh and the reveal would then claim zero.
    /// </summary>
    int? AnsweredPoints);

public record JoinResult(bool Ok, string? Error, StateSnapshot? Snapshot);

public record AnswerResult(
    bool Accepted,
    string? Reason,
    int PointsAwarded,
    int TotalScore,
    bool WasDuplicate);

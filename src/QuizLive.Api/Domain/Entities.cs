using System.ComponentModel.DataAnnotations;

namespace QuizLive.Domain;

public class Quiz
{
    public int Id { get; set; }
    [MaxLength(120)]
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public List<Question> Questions { get; set; } = new();
}

public class Question
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public Quiz? Quiz { get; set; }

    /// <summary>Position in the quiz, zero-based.</summary>
    public int Order { get; set; }

    [MaxLength(300)]
    public string Text { get; set; } = string.Empty;

    /// <summary>The answer options, stored as JSON so a question is a single row.</summary>
    public string OptionsJson { get; set; } = "[]";

    public int CorrectIndex { get; set; }
    public int TimeLimitSeconds { get; set; } = 20;
}

public enum GameState
{
    Lobby,
    Question,
    Reveal,
    Finished
}

public class GameSession
{
    public int Id { get; set; }

    /// <summary>Short code players type to join.</summary>
    [MaxLength(8)]
    public string Code { get; set; } = string.Empty;

    public int QuizId { get; set; }
    public Quiz? Quiz { get; set; }

    public GameState State { get; set; } = GameState.Lobby;

    /// <summary>Index into the quiz's ordered questions; -1 before the first one.</summary>
    public int CurrentQuestionIndex { get; set; } = -1;

    /// <summary>
    /// When the server released the current question. This is the clock every
    /// score is derived from — a client-reported time would be trivially forged.
    /// </summary>
    public DateTimeOffset? QuestionStartedUtc { get; set; }

    public string? HostConnectionId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    public List<Player> Players { get; set; } = new();
}

public class Player
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public GameSession? Session { get; set; }

    [MaxLength(24)]
    public string Name { get; set; } = string.Empty;

    public int Score { get; set; }

    /// <summary>Current SignalR connection, or null while disconnected.</summary>
    public string? ConnectionId { get; set; }

    /// <summary>
    /// Secret handed to the client on join and presented again to reclaim this
    /// player after a reload or network drop.
    /// </summary>
    [MaxLength(40)]
    public string ReconnectToken { get; set; } = string.Empty;

    public DateTimeOffset JoinedUtc { get; set; }
}

public class Answer
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public int QuestionId { get; set; }
    public int PlayerId { get; set; }
    public Player? Player { get; set; }

    public int OptionIndex { get; set; }
    public int ElapsedMs { get; set; }
    public bool IsCorrect { get; set; }
    public int PointsAwarded { get; set; }
}

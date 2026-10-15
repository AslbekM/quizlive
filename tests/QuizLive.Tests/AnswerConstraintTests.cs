using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuizLive.Data;
using QuizLive.Domain;
using Xunit;

namespace QuizLive.Tests;

/// <summary>
/// The storage-level guarantee behind idempotent answers, tested on its own.
/// <see cref="Services.GameService"/> checks for an existing answer first, but
/// that check can be raced; this index is what cannot be. Asserting it here
/// means the guarantee is proven directly rather than inferred from a timing
/// test the database may well have serialised anyway.
/// </summary>
public class AnswerConstraintTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly QuizDb _db;

    // Real parent rows: SQLite enforces foreign keys, so orphan answers fail
    // on the FK long before the unique index gets a say.
    private readonly int _sessionId;
    private readonly int _questionId;
    private readonly int _secondQuestionId;
    private readonly int _playerId;
    private readonly int _otherPlayerId;
    private readonly int _quizId;

    public AnswerConstraintTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new QuizDb(new DbContextOptionsBuilder<QuizDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();

        var quiz = new Quiz
        {
            Title = "Fixture",
            CreatedUtc = DateTimeOffset.UtcNow,
            Questions =
            {
                new Question
                {
                    Order = 0,
                    Text = "First",
                    OptionsJson = JsonSerializer.Serialize(new[] { "a", "b" }),
                    CorrectIndex = 1
                },
                new Question
                {
                    Order = 1,
                    Text = "Second",
                    OptionsJson = JsonSerializer.Serialize(new[] { "a", "b" }),
                    CorrectIndex = 0
                }
            }
        };
        _db.Quizzes.Add(quiz);
        _db.SaveChanges();
        _quizId = quiz.Id;
        _questionId = quiz.Questions.Single(q => q.Order == 0).Id;
        _secondQuestionId = quiz.Questions.Single(q => q.Order == 1).Id;

        var session = new GameSession
        {
            Code = "FIXT01",
            QuizId = quiz.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
            Players =
            {
                new Player { Name = "Ann", ReconnectToken = "token-ann", JoinedUtc = DateTimeOffset.UtcNow },
                new Player { Name = "Bob", ReconnectToken = "token-bob", JoinedUtc = DateTimeOffset.UtcNow }
            }
        };
        _db.Sessions.Add(session);
        _db.SaveChanges();

        _sessionId = session.Id;
        _playerId = session.Players.Single(p => p.Name == "Ann").Id;
        _otherPlayerId = session.Players.Single(p => p.Name == "Bob").Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private Answer Row(int option, int points) => new()
    {
        SessionId = _sessionId,
        QuestionId = _questionId,
        PlayerId = _playerId,
        OptionIndex = option,
        ElapsedMs = 1000,
        IsCorrect = option == 1,
        PointsAwarded = points
    };

    [Fact]
    public async Task SecondAnswerForTheSamePlayerAndQuestion_IsRefusedByTheDatabase()
    {
        _db.Answers.Add(Row(option: 1, points: 950));
        await _db.SaveChangesAsync();

        _db.Answers.Add(Row(option: 1, points: 950));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    /// <summary>Even a different choice cannot slip a second row in.</summary>
    [Fact]
    public async Task ChangingTheChosenOption_IsStillRefused()
    {
        _db.Answers.Add(Row(option: 1, points: 950));
        await _db.SaveChangesAsync();

        _db.Answers.Add(Row(option: 0, points: 0));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task TheSamePlayerMayAnswerADifferentQuestion()
    {
        _db.Answers.Add(Row(1, 950));
        await _db.SaveChangesAsync();

        var next = Row(1, 900);
        next.QuestionId = _secondQuestionId;
        _db.Answers.Add(next);
        await _db.SaveChangesAsync();

        Assert.Equal(2, await _db.Answers.CountAsync());
    }

    [Fact]
    public async Task DifferentPlayersMayAnswerTheSameQuestion()
    {
        _db.Answers.Add(Row(1, 950));
        await _db.SaveChangesAsync();

        var other = Row(0, 0);
        other.PlayerId = _otherPlayerId;
        _db.Answers.Add(other);
        await _db.SaveChangesAsync();

        Assert.Equal(2, await _db.Answers.CountAsync());
    }

    [Fact]
    public async Task TwoSessionsUsingTheSameCode_AreRefused()
    {
        _db.Sessions.Add(new GameSession
        {
            Code = "FIXT01",
            QuizId = _quizId,
            CreatedUtc = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }
}

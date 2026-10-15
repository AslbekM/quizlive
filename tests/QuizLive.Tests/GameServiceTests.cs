using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using QuizLive.Data;
using QuizLive.Domain;
using QuizLive.Services;
using Xunit;

namespace QuizLive.Tests;

/// <summary>
/// Game rules against a real SQLite database, so the unique index that makes
/// answers idempotent is actually enforced, and against a fake clock, so
/// speed-based scoring can be asserted without sleeping.
/// </summary>
public class GameServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly QuizDb _db;
    private readonly FakeTimeProvider _clock;
    private readonly GameService _games;

    private const int TimeLimit = 20;

    public GameServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        _db = new QuizDb(new DbContextOptionsBuilder<QuizDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();

        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        _games = new GameService(_db, _clock);

        _db.Quizzes.Add(new Quiz
        {
            Title = "Test Quiz",
            CreatedUtc = _clock.GetUtcNow(),
            Questions =
            {
                new Question
                {
                    Order = 0,
                    Text = "Pick the second option",
                    OptionsJson = JsonSerializer.Serialize(new[] { "wrong", "right", "wrong", "wrong" }),
                    CorrectIndex = 1,
                    TimeLimitSeconds = TimeLimit
                },
                new Question
                {
                    Order = 1,
                    Text = "Pick the first option",
                    OptionsJson = JsonSerializer.Serialize(new[] { "right", "wrong" }),
                    CorrectIndex = 0,
                    TimeLimitSeconds = TimeLimit
                }
            }
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task<(string Code, int PlayerId, string Token)> StartedGameAsync(string name = "Ann")
    {
        var quizId = _db.Quizzes.Select(q => q.Id).First();
        var session = await _games.CreateSessionAsync(quizId);

        var join = await _games.JoinAsync(session.Code, name, null, "conn-1");
        Assert.True(join.Ok, join.Error);

        await _games.NextQuestionAsync(session.Code);
        return (session.Code, join.Snapshot!.You!.PlayerId, join.Snapshot.You!.ReconnectToken);
    }

    // --- Server-authoritative timing -------------------------------------

    /// <summary>
    /// Two identical submissions differing only in how long the player took
    /// must score differently. The caller never supplies a duration, so the
    /// gap can only come from the server's own measurement.
    /// </summary>
    [Fact]
    public async Task ScoreFollowsTheServerClock_NotAnythingTheClientSends()
    {
        var fast = await StartedGameAsync("Fast");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var fastResult = await _games.SubmitAnswerAsync(fast.Code, fast.PlayerId, 1);

        var slow = await StartedGameAsync("Slow");
        _clock.Advance(TimeSpan.FromSeconds(18));
        var slowResult = await _games.SubmitAnswerAsync(slow.Code, slow.PlayerId, 1);

        Assert.True(fastResult.Accepted);
        Assert.True(slowResult.Accepted);
        Assert.True(fastResult.PointsAwarded > slowResult.PointsAwarded,
            $"fast {fastResult.PointsAwarded} should beat slow {slowResult.PointsAwarded}");
    }

    [Fact]
    public async Task AnswerAfterTheTimeLimit_IsRejectedAndScoresNothing()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(TimeLimit + 5));

        var result = await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 1);

        Assert.False(result.Accepted);
        Assert.Equal(0, result.TotalScore);
    }

    [Fact]
    public async Task WrongAnswer_ScoresNothingButIsRecorded()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(2));

        var result = await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 0);

        Assert.True(result.Accepted);
        Assert.Equal(0, result.PointsAwarded);
        Assert.Equal(1, await _db.Answers.CountAsync());
    }

    // --- Idempotency -----------------------------------------------------

    /// <summary>
    /// The double-tap case. A second submission must not add points, must not
    /// create a second row, and must not change what was already banked.
    /// </summary>
    [Fact]
    public async Task AnsweringTwice_DoesNotScoreTwice()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));

        var first = await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 1);
        var second = await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 1);

        Assert.False(first.WasDuplicate);
        Assert.True(second.WasDuplicate);
        Assert.Equal(first.TotalScore, second.TotalScore);
        Assert.Equal(1, await _db.Answers.CountAsync());

        var score = await _db.Players.AsNoTracking().Where(p => p.Id == game.PlayerId)
            .Select(p => p.Score).FirstAsync();
        Assert.Equal(first.PointsAwarded, score);
    }

    /// <summary>
    /// Changing your mind is not allowed either: the first answer stands, and
    /// a later correct one cannot overwrite an earlier wrong one.
    /// </summary>
    [Fact]
    public async Task SecondAnswerCannotReplaceTheFirst()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));

        var wrong = await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 0);
        var correct = await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 1);

        Assert.Equal(0, wrong.PointsAwarded);
        Assert.True(correct.WasDuplicate);
        Assert.Equal(0, correct.TotalScore);
    }

    /// <summary>
    /// The replay-after-reconnect case: the same logical answer arriving twice
    /// at once, racing the fast-path check. The database constraint is what
    /// has to catch this, not the lookup.
    /// </summary>
    [Fact]
    public async Task ConcurrentDuplicateSubmissions_ScoreOnlyOnce()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Separate contexts on one database: two in-flight requests, neither
        // able to see the other's uncommitted insert.
        var dbA = new QuizDb(new DbContextOptionsBuilder<QuizDb>().UseSqlite(_conn).Options);
        var dbB = new QuizDb(new DbContextOptionsBuilder<QuizDb>().UseSqlite(_conn).Options);
        var gamesA = new GameService(dbA, _clock);
        var gamesB = new GameService(dbB, _clock);

        var results = await Task.WhenAll(
            gamesA.SubmitAnswerAsync(game.Code, game.PlayerId, 1),
            gamesB.SubmitAnswerAsync(game.Code, game.PlayerId, 1));

        dbA.Dispose();
        dbB.Dispose();

        Assert.All(results, r => Assert.True(r.Accepted));
        Assert.Single(results, r => r.WasDuplicate);
        Assert.Equal(1, await _db.Answers.CountAsync());

        var score = await _db.Players.AsNoTracking().Where(p => p.Id == game.PlayerId)
            .Select(p => p.Score).FirstAsync();
        var scoringRun = results.Single(r => !r.WasDuplicate);
        Assert.Equal(scoringRun.PointsAwarded, score);
    }

    // --- Reconnect -------------------------------------------------------

    [Fact]
    public async Task RejoiningWithTheToken_KeepsTheSamePlayerAndScore()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var answered = await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 1);

        await _games.MarkDisconnectedAsync("conn-1");
        var rejoin = await _games.JoinAsync(game.Code, "Ann", game.Token, "conn-2");

        Assert.True(rejoin.Ok, rejoin.Error);
        Assert.Equal(game.PlayerId, rejoin.Snapshot!.You!.PlayerId);
        Assert.Equal(answered.PointsAwarded, rejoin.Snapshot.You!.Score);
        Assert.Equal(1, rejoin.Snapshot.PlayerCount);
    }

    /// <summary>
    /// A reconnecting client needs to know what it already did, or it will
    /// render an answerable question the player has in fact answered.
    /// </summary>
    [Fact]
    public async Task SnapshotAfterReconnect_ReportsTheAnswerAlreadyGiven()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 1);

        var snapshot = await _games.SnapshotAsync(game.Code, game.PlayerId);

        Assert.Equal(1, snapshot!.You!.AnsweredOptionIndex);
    }

    /// <summary>
    /// Time keeps running while a player is away, so the snapshot has to hand
    /// back the real remaining time, not a fresh full-length timer.
    /// </summary>
    [Fact]
    public async Task SnapshotReportsRemainingTime_MinusTimeSpentDisconnected()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(8));

        var snapshot = await _games.SnapshotAsync(game.Code, game.PlayerId);

        Assert.NotNull(snapshot!.Question);
        Assert.InRange(snapshot.Question!.MsRemaining, 11_500, 12_500);
    }

    [Fact]
    public async Task ReconnectTokenFromAnotherRoom_DoesNotGrantEntry()
    {
        var first = await StartedGameAsync("Ann");
        var quizId = _db.Quizzes.Select(q => q.Id).First();
        var other = await _games.CreateSessionAsync(quizId);

        var join = await _games.JoinAsync(other.Code, "Ann", first.Token, "conn-9");

        // Unknown token in this room, so it falls through to a normal join and
        // creates a fresh player with no score rather than importing one.
        Assert.True(join.Ok, join.Error);
        Assert.NotEqual(first.PlayerId, join.Snapshot!.You!.PlayerId);
        Assert.Equal(0, join.Snapshot.You!.Score);
    }

    // --- Room and flow rules ---------------------------------------------

    [Fact]
    public async Task NewPlayersCannotJoinAfterTheGameStarts()
    {
        var game = await StartedGameAsync();

        var late = await _games.JoinAsync(game.Code, "Latecomer", null, "conn-3");

        Assert.False(late.Ok);
        Assert.NotNull(late.Error);
    }

    [Fact]
    public async Task DuplicateNamesInOneRoom_AreRejected()
    {
        var quizId = _db.Quizzes.Select(q => q.Id).First();
        var session = await _games.CreateSessionAsync(quizId);

        Assert.True((await _games.JoinAsync(session.Code, "Ann", null, "c1")).Ok);
        var second = await _games.JoinAsync(session.Code, "ann", null, "c2");

        Assert.False(second.Ok);
    }

    [Fact]
    public async Task UnknownRoomCode_IsRejected()
    {
        var join = await _games.JoinAsync("ZZZZZZ", "Ann", null, "c1");

        Assert.False(join.Ok);
        Assert.NotNull(join.Error);
    }

    [Fact]
    public async Task RunningPastTheLastQuestion_FinishesTheGame()
    {
        var quizId = _db.Quizzes.Select(q => q.Id).First();
        var session = await _games.CreateSessionAsync(quizId);
        await _games.JoinAsync(session.Code, "Ann", null, "c1");

        Assert.NotNull(await _games.NextQuestionAsync(session.Code));
        Assert.NotNull(await _games.NextQuestionAsync(session.Code));
        Assert.Null(await _games.NextQuestionAsync(session.Code));

        var snapshot = await _games.SnapshotAsync(session.Code, null);
        Assert.Equal(nameof(GameState.Finished), snapshot!.State);
    }

    [Fact]
    public async Task RevealReportsCorrectIndexAndPerOptionCounts()
    {
        var quizId = _db.Quizzes.Select(q => q.Id).First();
        var session = await _games.CreateSessionAsync(quizId);

        var ann = await _games.JoinAsync(session.Code, "Ann", null, "c1");
        var bob = await _games.JoinAsync(session.Code, "Bob", null, "c2");
        await _games.NextQuestionAsync(session.Code);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await _games.SubmitAnswerAsync(session.Code, ann.Snapshot!.You!.PlayerId, 1);
        await _games.SubmitAnswerAsync(session.Code, bob.Snapshot!.You!.PlayerId, 0);

        var reveal = await _games.RevealAsync(session.Code);

        Assert.Equal(1, reveal!.CorrectIndex);
        Assert.Equal(1, reveal.CountsPerOption[0]);
        Assert.Equal(1, reveal.CountsPerOption[1]);
        Assert.Equal("Ann", reveal.Leaderboard[0].Name);
    }

    [Fact]
    public async Task AnswersAreRejectedWhileNoQuestionIsOpen()
    {
        var quizId = _db.Quizzes.Select(q => q.Id).First();
        var session = await _games.CreateSessionAsync(quizId);
        var join = await _games.JoinAsync(session.Code, "Ann", null, "c1");

        var result = await _games.SubmitAnswerAsync(session.Code, join.Snapshot!.You!.PlayerId, 1);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task OptionIndexOutsideTheQuestion_IsRejected()
    {
        var game = await StartedGameAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False((await _games.SubmitAnswerAsync(game.Code, game.PlayerId, 99)).Accepted);
        Assert.False((await _games.SubmitAnswerAsync(game.Code, game.PlayerId, -1)).Accepted);
        Assert.Equal(0, await _db.Answers.CountAsync());
    }
}

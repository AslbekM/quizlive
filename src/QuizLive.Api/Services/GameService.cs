using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuizLive.Contracts;
using QuizLive.Data;
using QuizLive.Domain;

namespace QuizLive.Services;

/// <summary>
/// All game state transitions. The SignalR hub is a thin transport over this,
/// so the rules can be tested without a connection, and so every clock reading
/// comes from the injected <see cref="TimeProvider"/> rather than a caller.
/// </summary>
public class GameService
{
    // Ambiguous glyphs removed: players type these off a projector screen.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly QuizDb _db;
    private readonly TimeProvider _clock;

    public GameService(QuizDb db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private static string[] Options(Question q) =>
        JsonSerializer.Deserialize<string[]>(q.OptionsJson) ?? Array.Empty<string>();

    public async Task<GameSession> CreateSessionAsync(int quizId)
    {
        var session = new GameSession
        {
            Code = await NewCodeAsync(),
            QuizId = quizId,
            State = GameState.Lobby,
            CurrentQuestionIndex = -1,
            CreatedUtc = _clock.GetUtcNow()
        };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    private async Task<string> NewCodeAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = string.Concat(Enumerable.Range(0, 6)
                .Select(_ => CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)]));

            if (!await _db.Sessions.AnyAsync(s => s.Code == code))
                return code;
        }
        throw new InvalidOperationException("Could not allocate a free room code.");
    }

    private Task<GameSession?> LoadAsync(string code) =>
        _db.Sessions
            .Include(s => s.Quiz!).ThenInclude(q => q.Questions)
            .Include(s => s.Players)
            .FirstOrDefaultAsync(s => s.Code == code);

    private static List<Question> Ordered(GameSession s) =>
        s.Quiz!.Questions.OrderBy(q => q.Order).ToList();

    private static Question? Current(GameSession s)
    {
        var questions = Ordered(s);
        return s.CurrentQuestionIndex >= 0 && s.CurrentQuestionIndex < questions.Count
            ? questions[s.CurrentQuestionIndex]
            : null;
    }

    private static QuestionView ToView(Question q, int index, int total, int msRemaining) =>
        new(q.Id, index, total, q.Text, Options(q), q.TimeLimitSeconds, Math.Max(0, msRemaining));

    private static ScoreRow[] Leaderboard(GameSession session) =>
        session.Players
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Name)
            .Select(p => new ScoreRow(p.Id, p.Name, p.Score))
            .ToArray();

    /// <summary>
    /// Joins a room, or reclaims an existing player when a valid reconnect
    /// token is presented. Reclaiming keeps the original score: a refresh
    /// mid-game must not wipe it or hand out a second identity.
    /// </summary>
    public async Task<JoinResult> JoinAsync(string code, string name, string? reconnectToken, string connectionId)
    {
        var session = await LoadAsync(code);
        if (session is null)
            return new JoinResult(false, "No room with that code.", null);

        Player? player = null;

        if (!string.IsNullOrWhiteSpace(reconnectToken))
            player = session.Players.FirstOrDefault(p => p.ReconnectToken == reconnectToken);

        if (player is null)
        {
            if (session.State != GameState.Lobby)
                return new JoinResult(false, "That game has already started.", null);

            name = name.Trim();
            if (string.IsNullOrEmpty(name))
                return new JoinResult(false, "Pick a name first.", null);
            if (name.Length > 24) name = name[..24];

            if (session.Players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                return new JoinResult(false, "Someone in this room already has that name.", null);

            player = new Player
            {
                SessionId = session.Id,
                Name = name,
                Score = 0,
                ReconnectToken = Guid.NewGuid().ToString("N"),
                JoinedUtc = _clock.GetUtcNow()
            };
            session.Players.Add(player);
        }

        player.ConnectionId = connectionId;
        await _db.SaveChangesAsync();

        return new JoinResult(true, null, await SnapshotAsync(code, player.Id));
    }

    public async Task<bool> AttachHostAsync(string code, string connectionId)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Code == code);
        if (session is null) return false;

        session.HostConnectionId = connectionId;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Releases the next question and stamps the start time. That stamp is the
    /// only clock any score is ever derived from. Returns null once the quiz
    /// has run out of questions, leaving the session finished.
    /// </summary>
    public async Task<QuestionView?> NextQuestionAsync(string code)
    {
        var session = await LoadAsync(code);
        if (session is null) return null;

        var questions = Ordered(session);
        var next = session.CurrentQuestionIndex + 1;

        if (next >= questions.Count)
        {
            session.State = GameState.Finished;
            session.QuestionStartedUtc = null;
            await _db.SaveChangesAsync();
            return null;
        }

        session.CurrentQuestionIndex = next;
        session.State = GameState.Question;
        session.QuestionStartedUtc = _clock.GetUtcNow();
        await _db.SaveChangesAsync();

        var question = questions[next];
        return ToView(question, next, questions.Count, question.TimeLimitSeconds * 1000);
    }

    /// <summary>
    /// Records an answer. The caller supplies only which option was picked,
    /// never how long they took: elapsed time is measured here against the
    /// server's own start stamp, because a client-reported duration would be
    /// trivially forged into a perfect score.
    /// </summary>
    public async Task<AnswerResult> SubmitAnswerAsync(string code, int playerId, int optionIndex)
    {
        var session = await LoadAsync(code);
        if (session is null)
            return new AnswerResult(false, "No room with that code.", 0, 0, false);

        var question = Current(session);
        if (session.State != GameState.Question || question is null || session.QuestionStartedUtc is null)
            return new AnswerResult(false, "No question is open right now.", 0, 0, false);

        var player = session.Players.FirstOrDefault(p => p.Id == playerId);
        if (player is null)
            return new AnswerResult(false, "You are not in this room.", 0, 0, false);

        var elapsedMs = (int)(_clock.GetUtcNow() - session.QuestionStartedUtc.Value).TotalMilliseconds;

        if (!Scoring.IsWithinTimeLimit(elapsedMs, question.TimeLimitSeconds))
            return new AnswerResult(false, "Time is up for that question.", 0, player.Score, false);

        if (optionIndex < 0 || optionIndex >= Options(question).Length)
            return new AnswerResult(false, "That is not one of the options.", 0, player.Score, false);

        // Fast path: an answer already committed and visible to us.
        var already = await _db.Answers.AsNoTracking()
            .FirstOrDefaultAsync(a => a.SessionId == session.Id
                                   && a.QuestionId == question.Id
                                   && a.PlayerId == player.Id);
        if (already is not null)
            return new AnswerResult(true, "Already answered.", already.PointsAwarded, player.Score, true);

        var isCorrect = optionIndex == question.CorrectIndex;
        var points = Scoring.Points(isCorrect, elapsedMs, question.TimeLimitSeconds);

        _db.Answers.Add(new Answer
        {
            SessionId = session.Id,
            QuestionId = question.Id,
            PlayerId = player.Id,
            OptionIndex = optionIndex,
            ElapsedMs = elapsedMs,
            IsCorrect = isCorrect,
            PointsAwarded = points
        });
        player.Score += points;

        try
        {
            // One transaction: if the unique index rejects the answer, the score
            // increment rolls back with it. That pairing is what stops a
            // double-tap or a replayed message from scoring twice.
            await _db.SaveChangesAsync();
            return new AnswerResult(true, null, points, player.Score, false);
        }
        catch (DbUpdateException)
        {
            // Lost a race against our own earlier message. Discard the local
            // edits and report whatever actually landed.
            _db.ChangeTracker.Clear();

            var winner = await _db.Answers.AsNoTracking()
                .FirstOrDefaultAsync(a => a.SessionId == session.Id
                                       && a.QuestionId == question.Id
                                       && a.PlayerId == playerId);

            var score = await _db.Players.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => p.Score)
                .FirstOrDefaultAsync();

            return new AnswerResult(true, "Already answered.", winner?.PointsAwarded ?? 0, score, true);
        }
    }

    public async Task<RevealView?> RevealAsync(string code)
    {
        var session = await LoadAsync(code);
        if (session is null) return null;

        var question = Current(session);
        if (question is null) return null;

        session.State = GameState.Reveal;
        await _db.SaveChangesAsync();

        return await BuildRevealAsync(session, question);
    }

    private async Task<RevealView> BuildRevealAsync(GameSession session, Question question)
    {
        var answers = await _db.Answers.AsNoTracking()
            .Where(a => a.SessionId == session.Id && a.QuestionId == question.Id)
            .ToListAsync();

        var counts = new int[Options(question).Length];
        foreach (var answer in answers)
            if (answer.OptionIndex >= 0 && answer.OptionIndex < counts.Length)
                counts[answer.OptionIndex]++;

        return new RevealView(question.Id, question.CorrectIndex, counts, Leaderboard(session));
    }

    /// <summary>
    /// The whole visible state of a room. Sent on join and on every reconnect
    /// so a client that missed messages can render correctly without replaying
    /// them, including how much time is genuinely left on the open question.
    /// </summary>
    public async Task<StateSnapshot?> SnapshotAsync(string code, int? playerId)
    {
        var session = await LoadAsync(code);
        if (session is null) return null;

        var questions = Ordered(session);
        var question = Current(session);

        QuestionView? view = null;
        if (session.State == GameState.Question && question is not null && session.QuestionStartedUtc is not null)
        {
            var elapsed = (int)(_clock.GetUtcNow() - session.QuestionStartedUtc.Value).TotalMilliseconds;
            view = ToView(question, session.CurrentQuestionIndex, questions.Count,
                question.TimeLimitSeconds * 1000 - elapsed);
        }

        RevealView? reveal = null;
        if (session.State == GameState.Reveal && question is not null)
            reveal = await BuildRevealAsync(session, question);

        PlayerSelf? self = null;
        if (playerId is int id)
        {
            var player = session.Players.FirstOrDefault(p => p.Id == id);
            if (player is not null)
            {
                int? picked = null;
                int? earned = null;
                if (question is not null)
                {
                    var answer = await _db.Answers.AsNoTracking()
                        .Where(a => a.SessionId == session.Id && a.QuestionId == question.Id && a.PlayerId == id)
                        .Select(a => new { a.OptionIndex, a.PointsAwarded })
                        .FirstOrDefaultAsync();

                    picked = answer?.OptionIndex;
                    earned = answer?.PointsAwarded;
                }
                self = new PlayerSelf(player.Id, player.Name, player.Score, player.ReconnectToken, picked, earned);
            }
        }

        var answersIn = question is null
            ? 0
            : await _db.Answers.CountAsync(a => a.SessionId == session.Id && a.QuestionId == question.Id);

        return new StateSnapshot(
            session.Code,
            session.State.ToString(),
            session.Quiz?.Title ?? "Quiz",
            session.Players.Count,
            view,
            reveal,
            Leaderboard(session),
            self,
            answersIn);
    }

    public async Task MarkDisconnectedAsync(string connectionId)
    {
        var player = await _db.Players.FirstOrDefaultAsync(p => p.ConnectionId == connectionId);
        if (player is null) return;

        // Keep the player row and their score; only the socket is gone. Their
        // reconnect token is what brings them back to this exact row.
        player.ConnectionId = null;
        await _db.SaveChangesAsync();
    }
}

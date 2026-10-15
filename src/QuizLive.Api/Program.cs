using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuizLive.Data;
using QuizLive.Domain;
using QuizLive.Hubs;
using QuizLive.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<QuizDb>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
                      ?? "Data Source=quizlive.db"));

builder.Services.AddSignalR();

// Injected rather than called statically so tests can drive the clock and
// assert on speed-based scoring without sleeping.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<GameService>();

const string DevCors = "vite";
builder.Services.AddCors(options =>
    options.AddPolicy(DevCors, policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        // SignalR negotiates with credentials; without this the websocket
        // handshake fails in dev while plain fetches appear to work.
        .AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseCors(DevCors);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/quizzes", async (QuizDb db) =>
    await db.Quizzes
        .Select(q => new { q.Id, q.Title, Questions = q.Questions.Count })
        .ToListAsync());

app.MapPost("/api/sessions", async (CreateSessionRequest request, GameService games, QuizDb db) =>
{
    var quizId = request.QuizId > 0
        ? request.QuizId
        : await db.Quizzes.Select(q => q.Id).FirstOrDefaultAsync();

    if (quizId == 0) return Results.BadRequest(new { error = "No quiz to host." });
    if (!await db.Quizzes.AnyAsync(q => q.Id == quizId))
        return Results.NotFound(new { error = "No such quiz." });

    var session = await games.CreateSessionAsync(quizId);
    return Results.Ok(new { code = session.Code });
});

app.MapHub<QuizHub>("/hub");

// SPA fallback so a deep link such as /play/ABC123 serves the client shell.
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuizDb>();
    await db.Database.EnsureCreatedAsync();
    await SeedAsync(db);
}

app.Run();

static async Task SeedAsync(QuizDb db)
{
    if (await db.Quizzes.AnyAsync()) return;

    static Question Q(int order, string text, string[] options, int correct, int seconds = 20) =>
        new()
        {
            Order = order,
            Text = text,
            OptionsJson = JsonSerializer.Serialize(options),
            CorrectIndex = correct,
            TimeLimitSeconds = seconds
        };

    db.Quizzes.Add(new Quiz
    {
        Title = "General Knowledge Warm-Up",
        CreatedUtc = DateTimeOffset.UtcNow,
        Questions =
        {
            Q(0, "Which planet is closest to the Sun?",
                ["Venus", "Mercury", "Mars", "Earth"], 1),
            Q(1, "What is the capital of Poland?",
                ["Krakow", "Gdansk", "Warsaw", "Wroclaw"], 2),
            Q(2, "How many bits are in one byte?",
                ["4", "8", "16", "32"], 1, 15),
            Q(3, "Which language runs natively in a web browser?",
                ["Python", "C#", "JavaScript", "Ruby"], 2, 15),
            Q(4, "What does SQL stand for?",
                [
                    "Structured Query Language",
                    "Sequential Query Logic",
                    "Simple Question Language",
                    "Server Query Layer"
                ], 0)
        }
    });

    await db.SaveChangesAsync();
}

internal record CreateSessionRequest(int QuizId);

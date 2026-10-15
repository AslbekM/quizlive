using Microsoft.EntityFrameworkCore;
using QuizLive.Domain;

namespace QuizLive.Data;

public class QuizDb : DbContext
{
    public QuizDb(DbContextOptions<QuizDb> options) : base(options) { }

    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<GameSession> Sessions => Set<GameSession>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Answer> Answers => Set<Answer>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // One answer per player per question, enforced by the database rather
        // than by a check-then-insert in application code. A double-tap or a
        // replay after reconnect races that check; it cannot race this.
        b.Entity<Answer>()
            .HasIndex(a => new { a.SessionId, a.QuestionId, a.PlayerId })
            .IsUnique();

        b.Entity<GameSession>()
            .HasIndex(s => s.Code)
            .IsUnique();

        b.Entity<GameSession>()
            .HasMany(s => s.Players)
            .WithOne(p => p.Session!)
            .HasForeignKey(p => p.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Quiz>()
            .HasMany(q => q.Questions)
            .WithOne(q => q.Quiz!)
            .HasForeignKey(q => q.QuizId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Answer>()
            .HasOne(a => a.Player)
            .WithMany()
            .HasForeignKey(a => a.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Player>()
            .HasIndex(p => p.ReconnectToken);
    }
}

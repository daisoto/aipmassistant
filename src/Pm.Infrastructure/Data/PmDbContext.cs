using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using Pm.Domain;

namespace Pm.Infrastructure.Data;

public sealed class PmDbContextOptions
{
    /// <summary>
    /// Размерность колонки vector(N). Должна совпадать с IEmbeddingClient.Dimensions.
    /// Меняете модель эмбеддингов — меняйте это значение и пересоздавайте БД.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 256;
}

public sealed class PmDbContext(DbContextOptions<PmDbContext> options, PmDbContextOptions settings)
    : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<PostMeetingDoc> PostMeetings => Set<PostMeetingDoc>();
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();

    public int EmbeddingDimensions => settings.EmbeddingDimensions;

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Npgsql пишет в timestamptz только DateTimeOffset со смещением 0,
        // поэтому нормализуем к UTC на границе БД, а не разбрасываем ToUniversalTime по коду.
        builder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");

        b.Entity<Project>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<Source>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.ProjectId).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<Message>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.SourceId).HasMaxLength(128);
            e.Property(x => x.ProjectId).HasMaxLength(64);
            e.Property(x => x.AuthorName).HasMaxLength(120);
            e.Property(x => x.At).HasMaxLength(16);
            e.HasIndex(x => x.SourceId);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<WorkItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ProjectId).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(400);
            e.Property(x => x.Assignee).HasMaxLength(120);
            e.Property(x => x.DueQuote).HasMaxLength(200);
            e.Property(x => x.SupersededById).HasMaxLength(64);
            e.HasIndex(x => x.ProjectId);

            e.Property(x => x.Embedding)
                .HasColumnType($"vector({settings.EmbeddingDimensions})")
                .HasConversion(
                    v => new Vector(v),
                    v => v.ToArray(),
                    new ValueComparer<float[]>(
                        (a, c) => a != null && c != null && a.SequenceEqual(c),
                        v => v.Aggregate(0, (acc, f) => HashCode.Combine(acc, f.GetHashCode())),
                        v => v.ToArray()));

            e.OwnsMany(x => x.Revisions, r =>
            {
                r.ToTable("WorkItemRevisions");
                r.WithOwner().HasForeignKey("WorkItemId");
                r.HasKey(x => x.Id);
                r.Property(x => x.Id).HasMaxLength(64);
                r.Property(x => x.Op).HasConversion<string>().HasMaxLength(32);
                r.Property(x => x.SourceId).HasMaxLength(128);
            });

            e.OwnsMany(x => x.Evidence, ev =>
            {
                ev.ToTable("WorkItemEvidence");
                ev.WithOwner().HasForeignKey("WorkItemId");
                ev.Property(x => x.MessageId).HasMaxLength(128);
                ev.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
                ev.Property(x => x.SourceId).HasMaxLength(128);
            });
        });

        b.Entity<PostMeetingDoc>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ProjectId).HasMaxLength(64);
            e.Property(x => x.SourceId).HasMaxLength(128);
            e.HasIndex(x => x.ProjectId);

            e.OwnsMany(x => x.Sections, s =>
            {
                s.ToTable("PostMeetingSections");
                s.WithOwner().HasForeignKey("PostMeetingDocId");
                s.Property(x => x.Title).HasMaxLength(120);
            });
        });

        b.Entity<LlmCall>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Operation).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.ProjectId).HasMaxLength(64);
        });

        // Enum'ы храним строками: содержимое БД и сырые SQL-запросы остаются читаемыми
        // (SqlCandidateIndex фильтрует по 'Superseded', а не по магическому числу).
        b.Entity<Source>().Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        b.Entity<Message>().Property(x => x.AuthorSide).HasConversion<string>().HasMaxLength(32);
        b.Entity<WorkItem>().Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        b.Entity<WorkItem>().Property(x => x.Side).HasConversion<string>().HasMaxLength(32);
        b.Entity<WorkItem>().Property(x => x.DueKind).HasConversion<string>().HasMaxLength(32);
        b.Entity<WorkItem>().Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
    }
}

public sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTimeOffset>(
        v => v.ToUniversalTime(),
        v => v.ToUniversalTime());

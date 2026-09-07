using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Pm.Infrastructure.Data;

/// <summary>
/// Готовит базу к работе: включает расширение vector и создаёт схему.
///
/// Схема создаётся через EnsureCreated, а не через миграции: на четырёхдневном MVP
/// модель меняется по несколько раз в день, и держать миграции в актуальном состоянии
/// дороже, чем пересоздать базу. Перед пилотом на реальных данных — перейти на миграции
/// (`dotnet ef migrations add Initial` из папки src/Pm.Web).
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(PmDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // Расширение создаётся до схемы: колонка vector(N) без него не создастся.
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;", ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var created = await db.Database.EnsureCreatedAsync(ct);
        logger.LogInformation(created ? "Схема БД создана." : "Схема БД уже существует.");

        // Индексы для гибридного поиска кандидатов.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS ix_workitems_fts
            ON "WorkItems"
            USING GIN (to_tsvector('russian', coalesce("Title", '') || ' ' || coalesce("Body", '')));
            """, ct);
    }

    public static async Task RecreateAsync(PmDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await db.Database.EnsureDeletedAsync(ct);
        await InitializeAsync(db, logger, ct);
    }
}

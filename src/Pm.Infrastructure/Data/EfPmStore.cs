using Microsoft.EntityFrameworkCore;
using Pm.Application;
using Pm.Domain;

namespace Pm.Infrastructure.Data;

/// <summary>
/// Реализация состояния поверх EF Core. Все выборки сущностей трекера идут по ProjectId —
/// изоляция контекста между проектами обеспечивается тем, что другого входа к данным нет.
/// </summary>
public sealed class EfPmStore(PmDbContext db, IDbContextFactory<PmDbContext> contexts) : IPmStore
{
    public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<Project?> GetProjectAsync(string id, CancellationToken ct = default)
        => await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Source>> GetSourcesAsync(string projectId, CancellationToken ct = default)
        => await db.Sources.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

    public async Task<Source?> GetSourceAsync(string id, CancellationToken ct = default)
        => await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(string sourceId, CancellationToken ct = default)
        => await db.Messages.AsNoTracking()
            .Where(m => m.SourceId == sourceId)
            .OrderBy(m => m.Order)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Message>> GetMessagesByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToArray();
        if (list.Length == 0) return [];
        return await db.Messages.AsNoTracking()
            .Where(m => list.Contains(m.Id))
            .OrderBy(m => m.Order)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkItem>> GetItemsAsync(string projectId, CancellationToken ct = default)
        => await db.WorkItems.AsNoTracking()
            .Where(w => w.ProjectId == projectId)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(ct);

    public async Task<WorkItem?> GetItemAsync(string id, CancellationToken ct = default)
        => await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task<IReadOnlyList<PostMeetingDoc>> GetPostMeetingsAsync(
        string projectId, CancellationToken ct = default)
        => await db.PostMeetings.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task UpsertProjectAsync(Project project, CancellationToken ct = default)
    {
        var existing = await db.Projects.FirstOrDefaultAsync(p => p.Id == project.Id, ct);
        if (existing is null)
        {
            db.Projects.Add(project);
        }
        else
        {
            existing.Name = project.Name;
            existing.Description = project.Description;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AddSourceAsync(Source source, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        if (await db.Sources.AnyAsync(s => s.Id == source.Id, ct)) return;
        db.Sources.Add(source);
        db.Messages.AddRange(messages);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddItemAsync(WorkItem item, CancellationToken ct = default)
    {
        db.WorkItems.Add(item);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Резолвер работает с отсоединёнными сущностями, поэтому обновление переносит скаляры
    /// и дописывает только новые ревизии и evidence. Замена коллекций целиком приводила бы
    /// к попытке вставить строки с уже существующими ключами.
    /// </summary>
    public async Task UpdateItemAsync(WorkItem item, CancellationToken ct = default)
    {
        var tracked = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == item.Id, ct);
        if (tracked is null)
        {
            db.WorkItems.Add(item);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            return;
        }

        tracked.Kind = item.Kind;
        tracked.Title = item.Title;
        tracked.Body = item.Body;
        tracked.Side = item.Side;
        tracked.Assignee = item.Assignee;
        tracked.IsPromiseToClient = item.IsPromiseToClient;
        tracked.DueAt = item.DueAt;
        tracked.DueQuote = item.DueQuote;
        tracked.DueKind = item.DueKind;
        tracked.Status = item.Status;
        tracked.SupersededById = item.SupersededById;
        tracked.UpdatedAt = item.UpdatedAt;
        if (item.Embedding.Length > 0) tracked.Embedding = item.Embedding;

        var knownRevisions = tracked.Revisions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var revision in item.Revisions.Where(r => !knownRevisions.Contains(r.Id)))
            tracked.Revisions.Add(revision);

        var knownEvidence = tracked.Evidence.Select(e => e.MessageId).ToHashSet(StringComparer.Ordinal);
        foreach (var evidence in item.Evidence.Where(e => !knownEvidence.Contains(e.MessageId)))
            tracked.Evidence.Add(evidence);

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task AddPostMeetingAsync(PostMeetingDoc doc, CancellationToken ct = default)
    {
        db.PostMeetings.Add(doc);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Пишется отдельным контекстом и без токена отмены — намеренно.
    /// Через общий контекст запись откатывалась бы вместе с провалившимся прогоном,
    /// а с отменённым токеном SaveChanges бросал бы прямо из finally, подменяя исходное исключение.
    /// </summary>
    public async Task AddLlmCallAsync(LlmCall call, CancellationToken ct = default)
    {
        await using var isolated = await contexts.CreateDbContextAsync(CancellationToken.None);
        isolated.LlmCalls.Add(call);
        await isolated.SaveChangesAsync(CancellationToken.None);
    }

    public async Task MarkSourceIngestedAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null) return;
        source.Ingested = true;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<LlmCall>> GetLlmCallsAsync(int limit, CancellationToken ct = default)
        => await db.LlmCalls.AsNoTracking().OrderByDescending(c => c.At).Take(limit).ToListAsync(ct);

    public async Task ResetAsync(CancellationToken ct = default)
    {
        // "LlmCalls" в списке нет: журнал должен переживать очистку, иначе дельта между
        // прогонами Pm.Eval теряется вместе с состоянием.
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE "WorkItemRevisions", "WorkItemEvidence", "PostMeetingSections",
                           "PostMeetings", "WorkItems", "Messages", "Sources", "Projects"
            RESTART IDENTITY CASCADE;
            """, ct);
        db.ChangeTracker.Clear();
    }

    public async Task RunInTransactionAsync(Func<CancellationToken, Task> body, CancellationToken ct = default)
    {
        // ponytail: одна транзакция на весь прогон источника. Потолок — источник целиком
        // либо применяется, либо нет; если понадобится дожимать частично, резать по блокам Threader'а.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await body(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}

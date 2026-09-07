using Microsoft.Extensions.Logging;
using Pm.Application.Deadlines;
using Pm.Application.Text;
using Pm.Domain;

namespace Pm.Application.Pipeline;

public sealed class ResolverOptions
{
    /// <summary>Ниже этого порога кандидат считается новым без обращения к модели.</summary>
    public double FastPathThreshold { get; set; } = 0.55;

    /// <summary>
    /// Защита от переслияния: если модель предложила слить с сущностью, похожей слабее этого,
    /// решение принудительно заменяется на New. Потерянная договорённость хуже дубля —
    /// дубль PM удалит за две секунды, пропущенную задачу не заметит вообще.
    /// </summary>
    public double MergeGuardThreshold { get; set; } = 0.45;

    /// <summary>Сколько существующих сущностей показывать модели.</summary>
    public int CandidateLimit { get; set; } = 8;
}

public sealed record ResolveOutcome(ResolveOp Op, WorkItem? Item, string Reason, bool GuardTriggered);

/// <summary>
/// Слой, который превращает поток кандидатов в состояние проекта.
/// Без него стресс-тест даёт десять дублей, а RetailFlow — два взаимоисключающих требования по доставке.
/// </summary>
public sealed class Resolver(
    ICandidateIndex index,
    ILlmClient llm,
    IDeadlineParser deadlines,
    IPmStore store,
    ILogger<Resolver> logger,
    ResolverOptions? options = null)
{
    private readonly ResolverOptions _opt = options ?? new ResolverOptions();

    public async Task<ResolveOutcome> ResolveAsync(
        Project project,
        Source source,
        Candidate candidate,
        float[] embedding,
        IReadOnlyList<Message> context,
        CancellationToken ct = default)
    {
        var nearest = await index.FindAsync(
            project.Id, candidate.Title, candidate.Body, embedding, _opt.CandidateLimit, ct);

        var best = nearest.FirstOrDefault();

        // Быстрый путь: ничего похожего нет — заводим сущность, не тратя вызов модели.
        if (best is null || best.Score < _opt.FastPathThreshold)
        {
            var created = await CreateAsync(project, source, candidate, embedding, ct);
            return new ResolveOutcome(ResolveOp.New, created,
                best is null ? "Похожих сущностей нет" : $"Лучшая близость {best.Score:F2} ниже порога", false);
        }

        var decision = await llm.ResolveAsync(new ResolveRequest(project, candidate, nearest, context), ct);

        if (decision.Op == ResolveOp.Noise)
            return new ResolveOutcome(ResolveOp.Noise, null, decision.Reason, false);

        if (decision.Op == ResolveOp.New)
        {
            var created = await CreateAsync(project, source, candidate, embedding, ct);
            return new ResolveOutcome(ResolveOp.New, created, decision.Reason, false);
        }

        var target = nearest.FirstOrDefault(n => n.Item.Id == decision.TargetId);
        if (target is null)
        {
            logger.LogWarning("Резолвер указал неизвестную цель {Target}, создаём новую сущность", decision.TargetId);
            var created = await CreateAsync(project, source, candidate, embedding, ct);
            return new ResolveOutcome(ResolveOp.New, created, "Цель слияния не найдена среди кандидатов", true);
        }

        if (target.Score < _opt.MergeGuardThreshold)
        {
            logger.LogInformation(
                "Guard: слияние с «{Title}» отклонено, близость {Score:F2}", target.Item.Title, target.Score);
            var created = await CreateAsync(project, source, candidate, embedding, ct);
            return new ResolveOutcome(ResolveOp.New, created,
                $"Guard от переслияния: близость {target.Score:F2} < {_opt.MergeGuardThreshold:F2}", true);
        }

        return decision.Op switch
        {
            ResolveOp.Update => await ApplyUpdateAsync(source, target.Item, candidate, decision, ct),
            ResolveOp.Close => await ApplyCloseAsync(source, target.Item, candidate, decision, ct),
            ResolveOp.Supersede => await ApplySupersedeAsync(project, source, target.Item, candidate, embedding, decision, ct),
            _ => new ResolveOutcome(ResolveOp.Noise, null, decision.Reason, false)
        };
    }

    private async Task<WorkItem> CreateAsync(
        Project project, Source source, Candidate candidate, float[] embedding, CancellationToken ct)
    {
        var item = new WorkItem
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = project.Id,
            Kind = candidate.Kind,
            Title = candidate.Title.Trim(),
            Body = candidate.Body.Trim(),
            Side = candidate.Side,
            Assignee = candidate.Assignee?.Value,
            IsPromiseToClient = candidate.IsPromiseToClient,
            Embedding = embedding
        };

        ApplyDeadline(item, candidate.Deadline, source.OccurredOn);

        foreach (var mid in candidate.EvidenceMessageIds)
            item.Evidence.Add(new WorkItemEvidence { MessageId = mid, Role = EvidenceRole.Origin, SourceId = source.Id });

        item.Revisions.Add(new WorkItemRevision
        {
            Id = Guid.NewGuid().ToString("n"),
            Op = ResolveOp.New,
            Summary = $"Создано из «{source.Title}»",
            Reason = candidate.Rationale,
            SourceId = source.Id,
            EvidenceMessageIds = [.. candidate.EvidenceMessageIds]
        });

        await store.AddItemAsync(item, ct);
        return item;
    }

    private async Task<ResolveOutcome> ApplyUpdateAsync(
        Source source, WorkItem item, Candidate candidate, ResolveDecision decision, CancellationToken ct)
    {
        var changes = new List<string>();
        var patch = decision.Patch ?? new WorkItemPatch();

        var newDeadline = candidate.Deadline;
        if (patch.DeadlineQuote is { Length: > 0 } pq)
            newDeadline = new Attributed<string>(pq, pq, patch.DeadlineMessageId ?? candidate.EvidenceMessageIds.FirstOrDefault() ?? "");

        if (newDeadline is not null && !string.Equals(item.DueQuote, newDeadline.Quote, StringComparison.OrdinalIgnoreCase))
        {
            var before = item.DueQuote ?? "не был указан";
            ApplyDeadline(item, newDeadline, source.OccurredOn);
            changes.Add($"срок: {before} → {item.DueQuote}");
        }

        var assignee = patch.Assignee ?? candidate.Assignee?.Value;
        if (!string.IsNullOrWhiteSpace(assignee) && item.Assignee != assignee)
        {
            changes.Add($"исполнитель: {item.Assignee ?? "не был указан"} → {assignee}");
            item.Assignee = assignee;
        }

        if (patch.Title is { Length: > 0 } title && title != item.Title)
        {
            changes.Add("формулировка уточнена");
            item.Title = title;
        }

        if (patch.Body is { Length: > 0 } body && body != item.Body)
        {
            item.Body = string.IsNullOrWhiteSpace(item.Body) ? body : item.Body + "\n" + body;
            changes.Add("добавлены детали");
        }

        if (patch.Kind is { } kind && kind != item.Kind)
        {
            changes.Add($"тип: {item.Kind} → {kind}");
            item.Kind = kind;
        }

        if (patch.Side is { } side && side != item.Side)
        {
            changes.Add($"сторона: {item.Side} → {side}");
            item.Side = side;
        }

        if (candidate.IsPromiseToClient && !item.IsPromiseToClient)
        {
            item.IsPromiseToClient = true;
            changes.Add("помечено как обещание клиенту");
        }

        foreach (var mid in candidate.EvidenceMessageIds)
        {
            if (item.Evidence.Any(e => e.MessageId == mid)) continue;
            item.Evidence.Add(new WorkItemEvidence
            {
                MessageId = mid,
                Role = changes.Count == 0 ? EvidenceRole.Confirmation : EvidenceRole.Update,
                SourceId = source.Id
            });
        }

        item.Revisions.Add(new WorkItemRevision
        {
            Id = Guid.NewGuid().ToString("n"),
            Op = ResolveOp.Update,
            Summary = changes.Count == 0 ? "Подтверждено без изменений" : string.Join("; ", changes),
            Reason = decision.Reason,
            SourceId = source.Id,
            EvidenceMessageIds = [.. candidate.EvidenceMessageIds]
        });

        item.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateItemAsync(item, ct);
        return new ResolveOutcome(ResolveOp.Update, item, decision.Reason, false);
    }

    private async Task<ResolveOutcome> ApplyCloseAsync(
        Source source, WorkItem item, Candidate candidate, ResolveDecision decision, CancellationToken ct)
    {
        item.Status = ItemStatus.Done;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.Revisions.Add(new WorkItemRevision
        {
            Id = Guid.NewGuid().ToString("n"),
            Op = ResolveOp.Close,
            Summary = "Договорённость выполнена или снята",
            Reason = decision.Reason,
            SourceId = source.Id,
            EvidenceMessageIds = [.. candidate.EvidenceMessageIds]
        });

        await store.UpdateItemAsync(item, ct);
        return new ResolveOutcome(ResolveOp.Close, item, decision.Reason, false);
    }

    /// <summary>
    /// Требование заменено новым («Стоп, курьера оставить для Москвы»).
    /// Старое не удаляется, а помечается — история изменений и есть объяснимость для PM.
    /// </summary>
    private async Task<ResolveOutcome> ApplySupersedeAsync(
        Project project, Source source, WorkItem old, Candidate candidate,
        float[] embedding, ResolveDecision decision, CancellationToken ct)
    {
        var replacement = await CreateAsync(project, source, candidate, embedding, ct);

        old.Status = ItemStatus.Superseded;
        old.SupersededById = replacement.Id;
        old.UpdatedAt = DateTimeOffset.UtcNow;
        old.Revisions.Add(new WorkItemRevision
        {
            Id = Guid.NewGuid().ToString("n"),
            Op = ResolveOp.Supersede,
            Summary = $"Заменено на «{TextUtil.Shorten(replacement.Title, 80)}»",
            Reason = decision.Reason,
            SourceId = source.Id,
            EvidenceMessageIds = [.. candidate.EvidenceMessageIds]
        });
        await store.UpdateItemAsync(old, ct);

        replacement.Revisions.Add(new WorkItemRevision
        {
            Id = Guid.NewGuid().ToString("n"),
            Op = ResolveOp.Supersede,
            Summary = $"Заменяет «{TextUtil.Shorten(old.Title, 80)}»",
            Reason = decision.Reason,
            SourceId = source.Id,
            EvidenceMessageIds = [.. candidate.EvidenceMessageIds]
        });
        await store.UpdateItemAsync(replacement, ct);

        return new ResolveOutcome(ResolveOp.Supersede, replacement, decision.Reason, false);
    }

    private void ApplyDeadline(WorkItem item, Attributed<string>? deadline, DateOnly sourceDate)
    {
        if (deadline is null)
        {
            item.DueAt = null;
            item.DueQuote = null;
            item.DueKind = DueKind.None;
            return;
        }

        var parsed = deadlines.Parse(deadline.Quote, sourceDate);
        item.DueAt = parsed.At;
        item.DueKind = parsed.Kind;
        item.DueQuote = parsed.Kind == DueKind.None ? null : deadline.Quote;
    }
}

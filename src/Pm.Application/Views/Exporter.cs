using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pm.Domain;

namespace Pm.Application.Views;

/// <summary>
/// Выгрузка состояния наружу: JSON для стороннего сервиса, Markdown для человека.
/// Возвращает строки, а не пишет файлы, — в вебе это ответ на запрос, в CLI — файл на диске.
/// </summary>
public sealed class Exporter(IPmStore store)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Плоское представление сущности. Embedding намеренно не выгружается: 256 чисел на
    /// сущность раздувают файл втрое и никому за пределами резолвера не нужны.
    /// </summary>
    public sealed record ItemDto(
        string Id, string ProjectId, string Kind, string Title, string Body, string Side,
        string? Assignee, bool IsPromiseToClient,
        DateTimeOffset? DueAt, string? DueQuote, string DueKind,
        string Status, string? SupersededById,
        IReadOnlyList<string> EvidenceMessageIds,
        IReadOnlyList<RevisionDto> History,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public sealed record RevisionDto(string Op, string Summary, string Reason, string? SourceId, DateTimeOffset At);

    public async Task<string> TrackerJsonAsync(string projectId, CancellationToken ct = default)
    {
        var project = await store.GetProjectAsync(projectId, ct)
                      ?? throw new InvalidOperationException($"Проект {projectId} не найден");
        var items = await store.GetItemsAsync(projectId, ct);

        return JsonSerializer.Serialize(new
        {
            project.Id,
            project.Name,
            project.Description,
            ExportedAt = DateTimeOffset.UtcNow,
            Items = items.Select(ToDto).ToList()
        }, Json);
    }

    public async Task<string> TrackerMarkdownAsync(string projectId, CancellationToken ct = default)
    {
        var project = await store.GetProjectAsync(projectId, ct)
                      ?? throw new InvalidOperationException($"Проект {projectId} не найден");
        var items = await store.GetItemsAsync(projectId, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# Трекер — {project.Name}").AppendLine();
        sb.AppendLine(project.Description).AppendLine();
        sb.AppendLine($"Выгружено {DateTimeOffset.Now:dd.MM.yyyy HH:mm}, сущностей: {items.Count}.").AppendLine();

        // Те же срезы, что и в интерфейсе: выгрузка не должна показывать другую картину, чем экран.
        foreach (var view in TrackerViews.All)
        {
            var slice = TrackerViews.Filter(items, view);
            if (slice.Count == 0) continue;

            sb.AppendLine($"## {TrackerViews.Title(view)} ({slice.Count})").AppendLine();
            foreach (var item in slice) sb.AppendLine(Line(item));
            sb.AppendLine();
        }

        var rest = TrackerViews.Uncategorized(items);
        if (rest.Count > 0)
        {
            sb.AppendLine($"## Решения и зафиксированное ({rest.Count})").AppendLine();
            foreach (var item in rest) sb.AppendLine(Line(item));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Готовый post-meeting. Формируется на экране созвона и хранится в базе.</summary>
    public async Task<string> PostMeetingAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await store.GetSourceAsync(sourceId, ct)
                     ?? throw new InvalidOperationException($"Источник {sourceId} не найден");

        var doc = (await store.GetPostMeetingsAsync(source.ProjectId, ct))
            .Where(d => d.SourceId == sourceId)
            .MaxBy(d => d.CreatedAt);

        return doc?.RenderedText
               ?? throw new InvalidOperationException(
                   $"Для источника {sourceId} post-meeting ещё не сформирован.");
    }

    private static string Line(WorkItem item)
    {
        var sb = new StringBuilder("- ").Append(item.Title.TrimEnd('.', ' '));
        if (!string.IsNullOrWhiteSpace(item.Assignee)) sb.Append(" — ").Append(item.Assignee);

        if (item.DueKind == DueKind.Explicit && item.DueAt is { } due)
            sb.Append($" (срок {due.ToLocalTime():dd.MM HH:mm}, из цитаты «{item.DueQuote}»)");
        else if (item.DueKind == DueKind.Vague)
            sb.Append($" (срок не зафиксирован: «{item.DueQuote}»)");

        if (item.Status == ItemStatus.Superseded) sb.Append(" [заменено]");
        return sb.ToString();
    }

    private static ItemDto ToDto(WorkItem i) => new(
        i.Id, i.ProjectId, i.Kind.ToString(), i.Title, i.Body, i.Side.ToString(),
        i.Assignee, i.IsPromiseToClient,
        i.DueAt, i.DueQuote, i.DueKind.ToString(),
        i.Status.ToString(), i.SupersededById,
        i.Evidence.Select(e => e.MessageId).ToList(),
        i.Revisions.OrderBy(r => r.CreatedAt)
            .Select(r => new RevisionDto(r.Op.ToString(), r.Summary, r.Reason, r.SourceId, r.CreatedAt))
            .ToList(),
        i.CreatedAt, i.UpdatedAt);
}

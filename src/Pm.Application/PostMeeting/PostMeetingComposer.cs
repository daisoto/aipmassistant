using Pm.Domain;

namespace Pm.Application.PostMeeting;

/// <summary>
/// Раскладка post-meeting по пяти секциям формата Xpage — детерминированная.
/// Модель отвечает только за формулировку пункта (см. PhrasingGuard), но не за состав.
/// </summary>
public sealed class PostMeetingComposer(IPmStore store, PostMeetingRenderer renderer, PhrasingGuard guard)
{
    public const string KeyOutcomes = "Ключевые итоги";
    public const string YourSide = "С вашей стороны";
    public const string OurSide = "С нашей стороны";
    public const string Recorded = "Зафиксировали";
    public const string NeedsClarification = "Требует уточнения";

    public async Task<PostMeetingDoc> ComposeAsync(
        string sourceId, ILlmClient? polisher = null, CancellationToken ct = default)
    {
        var source = await store.GetSourceAsync(sourceId, ct)
                     ?? throw new InvalidOperationException($"Источник {sourceId} не найден");
        if (source.Kind != SourceKind.CallTranscript)
            throw new InvalidOperationException("Post-meeting формируется только по транскрипту созвона.");

        var messageIds = (await store.GetMessagesAsync(sourceId, ct)).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var items = await store.GetItemsAsync(source.ProjectId, ct);

        // В документ попадает только то, что затронуто именно этим созвоном.
        var touched = items
            .Where(i => i.Evidence.Any(e => messageIds.Contains(e.MessageId)))
            .ToList();

        var doc = new PostMeetingDoc
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = source.ProjectId,
            SourceId = source.Id,
            MeetingDate = source.OccurredOn
        };

        // Секции наполняются по очереди, и сущность попадает ровно в одну:
        // задача без срока не должна одновременно стоять в «С нашей стороны»
        // и в «Требует уточнения» — в примере Xpage такого дублирования нет.
        var placed = new HashSet<string>(StringComparer.Ordinal);

        doc.Sections.Add(Section(KeyOutcomes, KeyOutcomeItems(touched), placed, exclusive: false));
        doc.Sections.Add(Section(YourSide, touched.Where(IsClientTask), placed));
        doc.Sections.Add(Section(OurSide, touched.Where(IsOurTask), placed));
        doc.Sections.Add(Section(Recorded, touched.Where(IsRecorded), placed));
        doc.Sections.Add(Section(NeedsClarification, touched.Where(NeedsClarifying), placed));

        if (polisher is not null)
        {
            var polished = await polisher.PolishAsync(doc.Sections, ct);
            var check = guard.Check(doc.Sections, polished);
            if (check.Ok)
            {
                doc.Sections.Clear();
                doc.Sections.AddRange(polished);
                doc.PolishApplied = true;
            }
            else
            {
                doc.GuardViolations.AddRange(check.Violations);
            }
        }

        doc.RenderedText = renderer.Render(doc);
        await store.AddPostMeetingAsync(doc, ct);
        return doc;
    }

    private PostMeetingSection Section(
        string title, IEnumerable<WorkItem> items, HashSet<string> placed, bool exclusive = true)
    {
        var section = new PostMeetingSection { Title = title };
        var ordered = items
            .Where(i => !exclusive || !placed.Contains(i.Id))
            .OrderBy(i => i.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.CreatedAt);

        foreach (var item in ordered)
        {
            section.Bullets.Add(renderer.Bullet(item, includeAssigneeAndDue: title is YourSide or OurSide));
            if (exclusive) placed.Add(item.Id);
        }

        return section;
    }

    /// <summary>Главные решения встречи. Три-четыре пункта, иначе секция перестаёт быть итогами.</summary>
    private static IEnumerable<WorkItem> KeyOutcomeItems(IReadOnlyList<WorkItem> touched)
        => touched
            .Where(i => i.Kind is ItemKind.Decision
                        || (i.Kind == ItemKind.Requirement && i.Status != ItemStatus.Superseded))
            .OrderByDescending(i => i.Kind == ItemKind.Decision)
            .ThenBy(i => i.CreatedAt)
            .Take(4);

    private static bool IsClientTask(WorkItem i)
        => i.Status == ItemStatus.Open
           && (i.Side == Side.Client || i.Side == Side.Contractor)
           && i.Kind is ItemKind.Task or ItemKind.Dependency;

    private static bool IsOurTask(WorkItem i)
        => i.Status == ItemStatus.Open && i.Side == Side.Xpage && i.Kind == ItemKind.Task;

    private static bool IsRecorded(WorkItem i)
        => i.Kind == ItemKind.Decision
           || i.Status == ItemStatus.Superseded
           || (i.Kind == ItemKind.Requirement && i.DueKind == DueKind.Explicit);

    /// <summary>
    /// Открытые вопросы и всё, где срок не зафиксирован. Здесь реализуется запрет
    /// «не придумывать дедлайн»: недостающий срок не выдумывается, а всплывает пунктом документа.
    /// </summary>
    private static bool NeedsClarifying(WorkItem i)
        => i.Status == ItemStatus.Open
           && (i.Kind == ItemKind.OpenQuestion
               || i.DueKind == DueKind.Vague
               || (i.Kind == ItemKind.Task && i.DueKind == DueKind.None));
}

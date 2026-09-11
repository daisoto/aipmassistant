namespace Pm.Domain;

public sealed class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class Source
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public SourceKind Kind { get; set; }
    public string Title { get; set; } = "";

    /// <summary>
    /// Календарная дата источника. Обязательна: от неё считаются относительные сроки
    /// («завтра до 15:00», «до среды»). В исходных материалах дат нет — мы назначаем их сами
    /// и фиксируем в materials/*.json и в эталоне.
    /// </summary>
    public DateOnly OccurredOn { get; set; }

    public int Order { get; set; }

    /// <summary>Источник уже прогнан через пайплайн. Защищает от повторной обработки и дублей.</summary>
    public bool Ingested { get; set; }
}

public sealed class Message
{
    /// <summary>Стабильный внешний ключ вида "urbankey.cc.11-34". По нему эталон ссылается на сообщения.</summary>
    public string Id { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public int Order { get; set; }

    /// <summary>Время в чате ("11:34") или таймкод созвона ("01:55").</summary>
    public string? At { get; set; }

    public string AuthorName { get; set; } = "";
    public Side AuthorSide { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Единица трекера: задача, решение, требование, открытый вопрос или зависимость.</summary>
public sealed class WorkItem
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";

    public ItemKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public Side Side { get; set; }

    /// <summary>Заполняется только если исполнитель назван в тексте явно.</summary>
    public string? Assignee { get; set; }

    /// <summary>Обещание, данное клиенту («вернёмся», «ответим»). Отделяет внешний срок от внутреннего.</summary>
    public bool IsPromiseToClient { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    /// <summary>Дословная цитата, из которой получен срок. Без неё срока не существует.</summary>
    public string? DueQuote { get; set; }

    public DueKind DueKind { get; set; } = DueKind.None;
    public ItemStatus Status { get; set; } = ItemStatus.Open;
    public string? SupersededById { get; set; }

    public float[] Embedding { get; set; } = [];

    public List<WorkItemRevision> Revisions { get; set; } = [];
    public List<WorkItemEvidence> Evidence { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// История изменений сущности. Не служебная таблица: это ответ PM на вопрос
/// «откуда система это взяла» и содержимое карточки задачи в интерфейсе.
/// </summary>
public sealed class WorkItemRevision
{
    public string Id { get; set; } = "";
    public ResolveOp Op { get; set; }
    public string Summary { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> EvidenceMessageIds { get; set; } = [];
    public string? SourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkItemEvidence
{
    public string MessageId { get; set; } = "";
    public EvidenceRole Role { get; set; }
    public string? SourceId { get; set; }
}

public sealed class PostMeetingSection
{
    public string Title { get; set; } = "";
    public List<string> Bullets { get; set; } = [];
}

public sealed class PostMeetingDoc
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public DateOnly MeetingDate { get; set; }
    public List<PostMeetingSection> Sections { get; set; } = [];
    public string RenderedText { get; set; } = "";
    public bool PolishApplied { get; set; }
    public List<string> GuardViolations { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Журнал обращений к модели. Одна запись — одна попытка HTTP, а не один логический вызов:
/// отказ строгой схемы с последующим повтором в json_object даёт две записи, иначе факт
/// «провайдер не принимает json_schema» виден только в логе и живёт до перезапуска.
/// </summary>
public sealed class LlmCall
{
    public string Id { get; set; } = "";

    /// <summary>Общий на весь прогон источника: связывает записи трекера с породившими их вызовами.</summary>
    public string? CorrelationId { get; set; }

    public string Operation { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? SourceId { get; set; }

    /// <summary>Номер попытки, начиная с 1.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>json_schema | json_object | none — в каком режиме запрашивался ответ.</summary>
    public string SchemaMode { get; set; } = "none";

    public int PromptChars { get; set; }
    public int ResponseChars { get; set; }

    /// <summary>
    /// Реальные токены — заполняет только провайдер, который их возвращает. Символы стоимость
    /// не оценивают: отношение к токенам на русском плавает, и схема в PromptChars не учтена.
    /// </summary>
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    /// <summary>Сырой запрос и ответ. Пишутся, только если включён Llm:LogPayloads.</summary>
    public string? RequestJson { get; set; }
    public string? ResponseJson { get; set; }

    public long ElapsedMs { get; set; }
    public bool Failed { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

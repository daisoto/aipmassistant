using Pm.Domain;

namespace Pm.Application;

/// <summary>Значение, подтверждённое дословной цитатой из конкретного сообщения.</summary>
public sealed record Attributed<T>(T Value, string Quote, string SourceMessageId);

/// <summary>
/// Кандидат в сущности трекера — то, что модель увидела в блоке сообщений.
/// Важно: Deadline здесь — цитата («завтра до 15:00»), а не дата.
/// Арифметику дат делает <see cref="Deadlines.DeadlineParser"/>, а не модель.
/// </summary>
public sealed class Candidate
{
    public string TempId { get; set; } = "";
    public ItemKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public Side Side { get; set; }
    public Attributed<string>? Assignee { get; set; }
    public Attributed<string>? Deadline { get; set; }
    public bool IsPromiseToClient { get; set; }
    public List<string> EvidenceMessageIds { get; set; } = [];
    public string Rationale { get; set; } = "";
}

public sealed class ExtractionResult
{
    public List<Candidate> Candidates { get; set; } = [];
    public List<string> NoiseMessageIds { get; set; } = [];
}

public sealed record ExtractionRequest(
    Project Project,
    Source Source,
    IReadOnlyList<Message> Block,
    IReadOnlyList<WorkItem> KnownItems);

public sealed class WorkItemPatch
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public Side? Side { get; set; }
    public ItemKind? Kind { get; set; }
    public string? Assignee { get; set; }
    public string? DeadlineQuote { get; set; }
    public string? DeadlineMessageId { get; set; }
    public bool? IsPromiseToClient { get; set; }
    public ItemStatus? Status { get; set; }
}

public sealed record ResolveDecision(
    ResolveOp Op,
    string? TargetId,
    WorkItemPatch? Patch,
    string Reason,
    double Confidence);

public sealed record ResolveRequest(
    Project Project,
    Candidate Candidate,
    IReadOnlyList<ScoredItem> Nearest,
    IReadOnlyList<Message> Context);

public sealed record ScoredItem(WorkItem Item, double Score, double Cosine, double Keyword);

/// <summary>Результат прогона пайплайна по одному источнику — то, что показывается в UI.</summary>
public sealed class IngestReport
{
    public string SourceId { get; set; } = "";
    public string SourceTitle { get; set; } = "";

    /// <summary>Ключ, по которому в журнале LLM находятся вызовы именно этого прогона.</summary>
    public string? CorrelationId { get; set; }

    public int MessageCount { get; set; }
    public int CandidateCount { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Superseded { get; set; }
    public int Closed { get; set; }
    public int Noise { get; set; }
    public int DeadlinesDropped { get; set; }
    public long ElapsedMs { get; set; }
    public List<string> Log { get; set; } = [];
}

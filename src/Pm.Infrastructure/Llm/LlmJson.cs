using System.Text.Json;
using System.Text.Json.Serialization;
using Pm.Application;
using Pm.Domain;

namespace Pm.Infrastructure.Llm;

/// <summary>
/// Разбор ответов модели в доменные типы. Общий для всех провайдеров: JSON-схемы
/// в <see cref="Prompts"/> одни и те же, поэтому и разбор должен быть один —
/// иначе два клиента расходятся в трактовке одного и того же поля.
/// </summary>
internal static class LlmJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static ExtractionResult ToExtraction(string json)
    {
        var dto = JsonSerializer.Deserialize<ExtractionDto>(json, Options) ?? new ExtractionDto();
        return new ExtractionResult
        {
            Candidates = dto.Candidates.Select(ToCandidate).ToList(),
            NoiseMessageIds = dto.NoiseMessageIds
        };
    }

    public static ResolveDecision ToDecision(string json)
    {
        var dto = JsonSerializer.Deserialize<ResolveDto>(json, Options);
        if (dto is null)
            return new ResolveDecision(ResolveOp.New, null, null, "Ответ резолвера не разобран", 0);

        var patch = dto.Patch is null
            ? null
            : new WorkItemPatch
            {
                Title = dto.Patch.Title,
                Body = dto.Patch.Body,
                Side = Parse<Side>(dto.Patch.Side),
                Kind = Parse<ItemKind>(dto.Patch.Kind),
                Assignee = dto.Patch.Assignee,
                DeadlineQuote = dto.Patch.DeadlineQuote,
                DeadlineMessageId = dto.Patch.DeadlineMessageId,
                IsPromiseToClient = dto.Patch.IsPromiseToClient,
                Status = Parse<ItemStatus>(dto.Patch.Status)
            };

        // Нераспознанная операция — это New: потерянная договорённость хуже дубля.
        return new ResolveDecision(
            Parse<ResolveOp>(dto.Op) ?? ResolveOp.New,
            dto.TargetId,
            patch,
            dto.Reason ?? "",
            dto.Confidence);
    }

    /// <summary>Не разобралось — возвращается черновик: полировка необязательна, документ обязателен.</summary>
    public static IReadOnlyList<PostMeetingSection> ToSections(
        string json, IReadOnlyList<PostMeetingSection> fallback)
    {
        var dto = JsonSerializer.Deserialize<PolishDto>(json, Options);
        return dto?.Sections.Select(s => new PostMeetingSection { Title = s.Title, Bullets = s.Bullets }).ToList()
               ?? fallback;
    }

    private static Candidate ToCandidate(CandidateDto d) => new()
    {
        TempId = d.TempId,
        Kind = Parse<ItemKind>(d.Kind) ?? ItemKind.Task,
        Title = d.Title,
        Body = d.Body,
        Side = Parse<Side>(d.Side) ?? Side.Xpage,
        IsPromiseToClient = d.IsPromiseToClient,
        EvidenceMessageIds = d.EvidenceMessageIds,
        Rationale = d.Rationale,
        Assignee = string.IsNullOrWhiteSpace(d.Assignee)
            ? null
            : new Attributed<string>(d.Assignee, d.AssigneeQuote ?? d.Assignee,
                d.DeadlineMessageId ?? d.EvidenceMessageIds.FirstOrDefault() ?? ""),
        Deadline = string.IsNullOrWhiteSpace(d.DeadlineQuote)
            ? null
            : new Attributed<string>(d.DeadlineQuote, d.DeadlineQuote,
                d.DeadlineMessageId ?? d.EvidenceMessageIds.FirstOrDefault() ?? "")
    };

    private static T? Parse<T>(string? value) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : null;

    private sealed class ExtractionDto
    {
        public List<CandidateDto> Candidates { get; set; } = [];
        public List<string> NoiseMessageIds { get; set; } = [];
    }

    private sealed class CandidateDto
    {
        public string TempId { get; set; } = "";
        public string? Kind { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string? Side { get; set; }
        public string? Assignee { get; set; }
        public string? AssigneeQuote { get; set; }
        public string? DeadlineQuote { get; set; }
        public string? DeadlineMessageId { get; set; }
        public bool IsPromiseToClient { get; set; }
        public List<string> EvidenceMessageIds { get; set; } = [];
        public string Rationale { get; set; } = "";
    }

    private sealed class ResolveDto
    {
        public string? Op { get; set; }
        public string? TargetId { get; set; }
        public PatchDto? Patch { get; set; }
        public string? Reason { get; set; }
        public double Confidence { get; set; }
    }

    private sealed class PatchDto
    {
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? Side { get; set; }
        public string? Kind { get; set; }
        public string? Assignee { get; set; }
        public string? DeadlineQuote { get; set; }
        public string? DeadlineMessageId { get; set; }
        public bool? IsPromiseToClient { get; set; }
        public string? Status { get; set; }
    }

    private sealed class PolishDto
    {
        public List<SectionDto> Sections { get; set; } = [];
    }

    private sealed class SectionDto
    {
        public string Title { get; set; } = "";
        public List<string> Bullets { get; set; } = [];
    }
}

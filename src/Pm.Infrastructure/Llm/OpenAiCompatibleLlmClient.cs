using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pm.Application;
using Pm.Domain;

namespace Pm.Infrastructure.Llm;

/// <summary>
/// Любой эндпоинт с /v1/chat/completions. На разработке — облачный, в бою — локальная модель
/// в контуре Xpage: меняется только BaseUrl, пайплайн об этом не знает.
///
/// Ответ запрашивается через response_format: json_schema со strict: true. Если провайдер
/// схему не поддерживает, включается json_object и разбор первого JSON-объекта из ответа.
/// </summary>
public sealed class OpenAiCompatibleLlmClient(
    HttpClient http,
    IOptions<LlmOptions> options,
    IPmStore store,
    ILogger<OpenAiCompatibleLlmClient> logger) : ILlmClient
{
    private readonly LlmOptions _opt = options.Value;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public string Name => $"{_opt.Model}@{_opt.BaseUrl}";

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default)
    {
        var json = await CompleteAsync(
            Prompts.ExtractionSystem,
            Prompts.ExtractionUser(request),
            "extraction",
            Prompts.ExtractionSchema,
            request.Project.Id,
            ct);

        var dto = JsonSerializer.Deserialize<ExtractionDto>(json, Json)
                  ?? new ExtractionDto();

        return new ExtractionResult
        {
            Candidates = dto.Candidates.Select(ToCandidate).ToList(),
            NoiseMessageIds = dto.NoiseMessageIds
        };
    }

    public async Task<ResolveDecision> ResolveAsync(ResolveRequest request, CancellationToken ct = default)
    {
        var json = await CompleteAsync(
            Prompts.ResolveSystem,
            Prompts.ResolveUser(request),
            "resolve",
            Prompts.ResolveSchema,
            request.Project.Id,
            ct);

        var dto = JsonSerializer.Deserialize<ResolveDto>(json, Json);
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

        return new ResolveDecision(
            Parse<ResolveOp>(dto.Op) ?? ResolveOp.New,
            dto.TargetId,
            patch,
            dto.Reason ?? "",
            dto.Confidence);
    }

    public async Task<IReadOnlyList<PostMeetingSection>> PolishAsync(
        IReadOnlyList<PostMeetingSection> draft, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { sections = draft }, Json);
        var json = await CompleteAsync(
            Prompts.PolishSystem, payload, "polish", Prompts.PolishSchema, null, ct);

        var dto = JsonSerializer.Deserialize<PolishDto>(json, Json);
        return dto?.Sections.Select(s => new PostMeetingSection { Title = s.Title, Bullets = s.Bullets }).ToList()
               ?? draft;
    }

    private async Task<string> CompleteAsync(
        string system, string user, string operation, string schema, string? projectId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var prompt = system.Length + user.Length;
        string? content = null;
        string? error = null;

        try
        {
            content = await SendAsync(system, user, schema, strict: true, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Строгая схема не принята провайдером, повтор в режиме json_object");
            try
            {
                content = await SendAsync(system, user, schema, strict: false, ct);
            }
            catch (Exception inner)
            {
                error = inner.Message;
                throw;
            }
        }
        finally
        {
            await store.AddLlmCallAsync(new LlmCall
            {
                Id = Guid.NewGuid().ToString("n"),
                Operation = operation,
                Provider = Name,
                ProjectId = projectId,
                PromptChars = prompt,
                ResponseChars = content?.Length ?? 0,
                ElapsedMs = sw.ElapsedMilliseconds,
                Failed = error is not null,
                Error = error
            }, ct);
        }

        return ExtractJson(content!);
    }

    private async Task<string> SendAsync(
        string system, string user, string schema, bool strict, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _opt.Model,
            ["temperature"] = _opt.Temperature,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user })
        };

        body["response_format"] = strict
            ? new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "pm_assistant",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(schema)
                }
            }
            : new JsonObject { ["type"] = "json_object" };

        var response = await http.PostAsJsonAsync("chat/completions", body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"LLM ответила {(int)response.StatusCode}: {detail}", null, response.StatusCode);
        }

        var parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(ct)
                     ?? throw new JsonException("Пустой ответ модели.");

        return parsed.Choices.FirstOrDefault()?.Message.Content
               ?? throw new JsonException("Ответ модели не содержит content.");
    }

    /// <summary>Некоторые локальные модели оборачивают JSON в ```json — вырезаем первый объект.</summary>
    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
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

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice> Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    private sealed record ChatMessage(
        [property: JsonPropertyName("content")] string? Content);

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

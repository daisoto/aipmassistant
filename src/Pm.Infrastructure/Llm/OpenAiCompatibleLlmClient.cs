using System.Diagnostics;
using System.Net;
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
/// схему не поддерживает, ограничение снимается совсем и из ответа разбирается первый JSON-объект.
/// </summary>
public sealed class OpenAiCompatibleLlmClient(
    HttpClient http,
    IOptions<LlmOptions> options,
    IPmStore store,
    LlmRunContext run,
    ILogger<OpenAiCompatibleLlmClient> logger) : ILlmClient
{
    private readonly LlmOptions _opt = options.Value;

    public string Name => $"{_opt.Model}@{_opt.BaseUrl}";

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default)
        => LlmJson.ToExtraction(await CompleteAsync(
            Prompts.ExtractionSystem,
            Prompts.ExtractionUser(request),
            "extraction",
            Prompts.ExtractionSchema,
            request.Project.Id,
            ct));

    public async Task<ResolveDecision> ResolveAsync(ResolveRequest request, CancellationToken ct = default)
        => LlmJson.ToDecision(await CompleteAsync(
            Prompts.ResolveSystem,
            Prompts.ResolveUser(request),
            "resolve",
            Prompts.ResolveSchema,
            request.Project.Id,
            ct));

    public async Task<IReadOnlyList<PostMeetingSection>> PolishAsync(
        IReadOnlyList<PostMeetingSection> draft, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { sections = draft }, LlmJson.Options);
        var json = await CompleteAsync(
            Prompts.PolishSystem, payload, "polish", Prompts.PolishSchema, null, ct);

        return LlmJson.ToSections(json, draft);
    }

    /// <summary>Попыток на каждый режим ответа. Три при паузах 1/2/4 с — минута на переживание 429.</summary>
    private const int MaxAttemptsPerMode = 3;

    /// <summary>
    /// Два режима подряд — json_schema, затем без ограничения; внутри каждого до трёх попыток.
    /// Повторяются только сетевые сбои, таймауты, 429 и 5xx: 4xx означает, что режим
    /// провайдером не поддерживается, и повтор в нём бессмысленен.
    /// </summary>
    private async Task<string> CompleteAsync(
        string system, string user, string operation, string schema, string? projectId, CancellationToken ct)
    {
        var attempt = 0;
        Exception? last = null;

        foreach (var strict in new[] { true, false })
        {
            for (var i = 0; i < MaxAttemptsPerMode; i++)
            {
                try
                {
                    return ExtractJson(await AttemptAsync(
                        system, user, schema, strict, operation, projectId, ++attempt, ct));
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException
                                           && !ct.IsCancellationRequested)
                {
                    last = ex;
                    if (!Retryable(ex))
                    {
                        logger.LogWarning(ex, "Режим {Mode} провайдером не принят, перехожу к следующему",
                            strict ? "json_schema" : "без ограничения");
                        break;
                    }

                    if (i + 1 == MaxAttemptsPerMode) break;
                    var delay = Backoff(ex, i);
                    logger.LogWarning("Попытка {Attempt} не удалась ({Error}), повтор через {Delay}",
                        attempt, ex.Message, delay);
                    await Task.Delay(delay, ct);
                }
            }
        }

        throw last ?? new InvalidOperationException("LLM не вернула ответ.");
    }

    /// <summary>Одна попытка: HTTP-запрос плюс запись в журнал — она пишется и при успехе, и при отказе.</summary>
    private async Task<string> AttemptAsync(
        string system, string user, string schema, bool strict,
        string operation, string? projectId, int attempt, CancellationToken ct)
    {
        var body = BuildBody(system, user, schema, strict);
        var sw = Stopwatch.StartNew();
        string? content = null;
        string? error = null;

        try
        {
            content = await SendAsync(body, ct);
            return content;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            await LogAsync(new LlmCall
            {
                Id = Guid.NewGuid().ToString("n"),
                CorrelationId = run.CorrelationId,
                Operation = operation,
                Provider = Name,
                ProjectId = projectId ?? run.ProjectId,
                SourceId = run.SourceId,
                Attempt = attempt,
                SchemaMode = strict ? "json_schema" : "none",
                PromptChars = system.Length + user.Length + (strict ? schema.Length : 0),
                ResponseChars = content?.Length ?? 0,
                RequestJson = _opt.LogPayloads ? body.ToJsonString() : null,
                ResponseJson = _opt.LogPayloads ? content : null,
                ElapsedMs = sw.ElapsedMilliseconds,
                Failed = error is not null,
                Error = error
            });
        }
    }

    /// <summary>Отказ журнала не должен подменять собой отказ модели, поэтому глушится здесь.</summary>
    private async Task LogAsync(LlmCall call)
    {
        try
        {
            await store.AddLlmCallAsync(call, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось записать вызов LLM в журнал");
        }
    }

    private JsonObject BuildBody(string system, string user, string schema, bool strict)
    {
        var body = new JsonObject
        {
            ["model"] = _opt.Model,
            ["temperature"] = _opt.Temperature,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user })
        };

        // ponytail: во втором режиме ограничение снимается совсем, а не меняется на json_object —
        // его понимают не все провайдеры: LM Studio отвечает 400 «must be json_schema or text».
        // Отсутствие поля принимают все, форму держит промпт, обёртку ```json снимает ExtractJson.
        if (strict)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "pm_assistant",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(schema)
                }
            };

        return body;
    }

    private async Task<string> SendAsync(JsonObject body, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("chat/completions", body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new LlmHttpException(
                $"LLM ответила {(int)response.StatusCode}: {detail}",
                response.StatusCode,
                response.Headers.RetryAfter?.Delta);
        }

        var parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(ct)
                     ?? throw new JsonException("Пустой ответ модели.");

        var content = parsed.Choices.FirstOrDefault()?.Message.Content;

        // Пустая строка, а не null — штатный ответ reasoning-модели, которая израсходовала
        // бюджет на рассуждения и до content не дошла: HTTP 200, finish_reason stop, пусто.
        // Проверка на null её пропускала, и отказ всплывал невнятной ошибкой разбора JSON.
        return string.IsNullOrWhiteSpace(content)
            ? throw new JsonException("Ответ модели не содержит content.")
            : content;
    }

    /// <summary>Таймаут и сетевой сбой лечатся повтором, 429 и 5xx — тоже; остальные 4xx — нет.</summary>
    private static bool Retryable(Exception ex) => ex switch
    {
        TaskCanceledException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var code } =>
            code is HttpStatusCode.TooManyRequests || (int)code! >= 500,
        _ => false
    };

    private static TimeSpan Backoff(Exception ex, int attemptIndex)
        => ex is LlmHttpException { RetryAfter: { } wait } && wait > TimeSpan.Zero
            ? wait
            : TimeSpan.FromSeconds(1 << attemptIndex);

    /// <summary>
    /// Некоторые локальные модели оборачивают JSON в ```json — вырезаем первый объект.
    /// Публичный, потому что это чистая функция и главный источник тихих поломок разбора.
    /// </summary>
    public static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    /// <summary>HttpRequestException, донёсший Retry-After: без него пауза считалась бы вслепую.</summary>
    private sealed class LlmHttpException(string message, HttpStatusCode? status, TimeSpan? retryAfter)
        : HttpRequestException(message, null, status)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice> Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    private sealed record ChatMessage(
        [property: JsonPropertyName("content")] string? Content);
}

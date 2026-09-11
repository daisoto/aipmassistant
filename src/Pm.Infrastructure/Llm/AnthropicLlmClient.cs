using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pm.Application;
using Pm.Domain;
using LlmCall = Pm.Domain.LlmCall;

namespace Pm.Infrastructure.Llm;

/// <summary>
/// Claude через официальный SDK. Отдельный клиент, а не BaseUrl у OpenAiCompatible:
/// у Anthropic нет эндпоинта /v1/chat/completions, форма запроса другая.
///
/// Строгий JSON обеспечивается output_config.format — схемы из <see cref="Prompts"/>
/// уже совместимы (additionalProperties: false и полный required), поэтому
/// fallback-режима json_object здесь нет и не нужно.
/// </summary>
public sealed class AnthropicLlmClient(
    AnthropicClient client,
    IOptions<LlmOptions> options,
    IPmStore store,
    LlmRunContext run,
    ILogger<AnthropicLlmClient> logger) : ILlmClient
{
    /// <summary>Ответы — компактный JSON; потолок нужен, чтобы длинный блок не обрезался на середине.</summary>
    private const int MaxTokens = 16000;

    private const string DefaultModel = "claude-sonnet-5";

    private readonly LlmOptions _opt = options.Value;

    public string Name => $"{Model}@anthropic";

    /// <summary>
    /// Llm:Model в конфиге общий для всех провайдеров и по умолчанию содержит имя локальной
    /// модели. Чужое имя здесь дало бы 404 без внятной причины, поэтому подставляем Opus.
    /// </summary>
    private string Model
    {
        get
        {
            if (_opt.Model.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return _opt.Model;
            logger.LogWarning(
                "Llm:Model = «{Model}» не похоже на модель Anthropic, использую {Default}", _opt.Model, DefaultModel);
            return DefaultModel;
        }
    }

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

    private async Task<string> CompleteAsync(
        string system, string user, string operation, string schema, string? projectId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? content = null;
        string? error = null;
        Usage? usage = null;

        try
        {
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = MaxTokens,
                System = system,
                Messages = [new() { Role = Role.User, Content = user }],
                // Схема из Prompts как есть: строка уже описывает объект целиком.
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat
                    {
                        Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema)!
                    }
                }
                // Thinking не задаём: на Opus 5 адаптивное включено по умолчанию.
                // Temperature не передаём вовсе — на этом семействе параметр удалён и даёт 400.
            }, cancellationToken: ct);

            usage = response.Usage;

            // Отказ приходит как HTTP 200 с пустым содержимым — без этой проверки
            // источник молча получил бы ноль кандидатов вместо ошибки.
            if (response.StopReason == "refusal")
            {
                var reason = response.StopDetails is { } d ? $"{d.Category}: {d.Explanation}" : "без пояснения";
                throw new InvalidOperationException($"Модель отклонила запрос ({reason}).");
            }

            content = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Ответ модели не содержит текста.");

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
                SchemaMode = "json_schema",
                PromptChars = system.Length + user.Length + schema.Length,
                ResponseChars = content?.Length ?? 0,
                InputTokens = (int?)usage?.InputTokens,
                OutputTokens = (int?)usage?.OutputTokens,
                RequestJson = _opt.LogPayloads ? user : null,
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
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pm.Domain;

namespace Pm.Application.Pipeline;

/// <summary>
/// Оркестратор: загрузка → тредирование → извлечение → валидация цитат →
/// разбор сроков → резолвинг → применение с записью ревизий.
/// </summary>
public sealed class PipelineRunner(
    IPmStore store,
    ILlmClient llm,
    IEmbeddingClient embeddings,
    Resolver resolver,
    QuoteValidator quoteValidator,
    Threader threader,
    ILogger<PipelineRunner> logger)
{
    public async Task<IngestReport> RunSourceAsync(string sourceId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var source = await store.GetSourceAsync(sourceId, ct)
                     ?? throw new InvalidOperationException($"Источник {sourceId} не найден");
        var project = await store.GetProjectAsync(source.ProjectId, ct)
                      ?? throw new InvalidOperationException($"Проект {source.ProjectId} не найден");

        var messages = await store.GetMessagesAsync(sourceId, ct);
        var report = new IngestReport
        {
            SourceId = source.Id,
            SourceTitle = source.Title,
            MessageCount = messages.Count
        };

        if (source.Ingested)
        {
            report.Log.Add("Источник уже обработан — пропущен.");
            report.ElapsedMs = sw.ElapsedMilliseconds;
            return report;
        }

        foreach (var block in threader.Split(messages))
        {
            var known = await store.GetItemsAsync(project.Id, ct);
            var extraction = await llm.ExtractAsync(new ExtractionRequest(project, source, block, known), ct);

            var validation = quoteValidator.Validate(extraction, block);
            report.DeadlinesDropped += validation.DeadlinesDropped;
            report.Log.AddRange(validation.Log);
            report.Noise += extraction.NoiseMessageIds.Count;
            report.CandidateCount += extraction.Candidates.Count;

            foreach (var candidate in extraction.Candidates)
            {
                ct.ThrowIfCancellationRequested();

                var embedding = await embeddings.EmbedAsync($"{candidate.Title}. {candidate.Body}", ct);
                var outcome = await resolver.ResolveAsync(project, source, candidate, embedding, block, ct);

                switch (outcome.Op)
                {
                    case ResolveOp.New: report.Created++; break;
                    case ResolveOp.Update: report.Updated++; break;
                    case ResolveOp.Supersede: report.Superseded++; break;
                    case ResolveOp.Close: report.Closed++; break;
                    case ResolveOp.Noise: report.Noise++; break;
                }

                if (outcome.GuardTriggered)
                    report.Log.Add($"Guard от переслияния сработал на «{candidate.Title}».");
            }
        }

        await store.MarkSourceIngestedAsync(source.Id, ct);

        report.ElapsedMs = sw.ElapsedMilliseconds;
        logger.LogInformation(
            "Источник {Source}: кандидатов {Cand}, создано {New}, обновлено {Upd}, заменено {Sup}, шум {Noise}, {Ms} мс",
            source.Title, report.CandidateCount, report.Created, report.Updated, report.Superseded, report.Noise,
            report.ElapsedMs);

        return report;
    }

    /// <summary>Прогон всех необработанных источников проекта в порядке загрузки.</summary>
    public async Task<List<IngestReport>> RunProjectAsync(string projectId, CancellationToken ct = default)
    {
        var reports = new List<IngestReport>();
        var sources = (await store.GetSourcesAsync(projectId, ct)).OrderBy(s => s.Order).ToList();
        foreach (var source in sources)
            reports.Add(await RunSourceAsync(source.Id, ct));
        return reports;
    }
}

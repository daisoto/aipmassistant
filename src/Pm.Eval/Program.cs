using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pm.Application;
using Pm.Application.Pipeline;
using Pm.Domain;
using Pm.Eval;
using Pm.Infrastructure;
using Pm.Infrastructure.Data;
using Pm.Infrastructure.Golden;
using Pm.Infrastructure.Materials;

// Прогонщик метрик: загружает материалы, прогоняет пайплайн, сопоставляет с эталоном,
// печатает таблицу и пишет отчёт в reports/.
//
//   dotnet run --project src/Pm.Eval -- --set dev
//   dotnet run --project src/Pm.Eval -- --set holdout --recreate

var setName = GetArg("--set") ?? "dev";
var recreate = args.Contains("--recreate");

var configuration = new ConfigurationBuilder()
    .SetBasePath(RepoPaths.Root)
    .AddJsonFile(Path.Combine("src", "Pm.Web", "appsettings.json"), optional: false)
    .AddJsonFile(Path.Combine("src", "Pm.Web", "appsettings.Development.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddPmAssistant(configuration);
await using var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();
var sp = scope.ServiceProvider;
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pm.Eval");
var db = sp.GetRequiredService<PmDbContext>();

Console.WriteLine($"Корень репозитория: {RepoPaths.Root}");

if (recreate) await DbInitializer.RecreateAsync(db, logger);
else await DbInitializer.InitializeAsync(db, logger);

var store = sp.GetRequiredService<IPmStore>();
await store.ResetAsync();

var golden = await GoldenLoader.LoadAsync();
var projectIds = golden.ProjectsOf(setName).ToList();
if (projectIds.Count == 0)
{
    Console.Error.WriteLine(
        $"В golden/ нет проектов набора «{setName}». Доступны: " +
        string.Join(", ", golden.Projects.Select(p => $"{p.ProjectId}({p.Set})")));
    return 1;
}

Console.WriteLine($"Набор «{setName}»: проекты {string.Join(", ", projectIds)}");

await sp.GetRequiredService<MaterialLoader>().LoadAllAsync();

var pipeline = sp.GetRequiredService<PipelineRunner>();
var sw = Stopwatch.StartNew();

// Основные источники проектов набора — без стресс-теста.
foreach (var projectId in projectIds)
{
    var sources = (await store.GetSourcesAsync(projectId))
        .Where(s => !golden.Stress.SourceIds.Contains(s.Id))
        .OrderBy(s => s.Order);

    foreach (var source in sources)
    {
        var report = await pipeline.RunSourceAsync(source.Id);
        Console.WriteLine(
            $"  {source.Title,-28} кандидатов {report.CandidateCount,3}  " +
            $"новых {report.Created,3}  обновлений {report.Updated,3}  " +
            $"замен {report.Superseded,2}  шум {report.Noise,3}");
    }
}

// Стресс-тест прогоняется отдельно: корректный результат — ноль новых сущностей.
var beforeStress = new Dictionary<string, int>(StringComparer.Ordinal);
foreach (var projectId in projectIds)
    beforeStress[projectId] = (await store.GetItemsAsync(projectId)).Count;

var stressNew = 0;
foreach (var projectId in projectIds)
{
    foreach (var sourceId in golden.Stress.SourceIds)
    {
        var source = await store.GetSourceAsync(sourceId);
        if (source is null || source.ProjectId != projectId) continue;
        var report = await pipeline.RunSourceAsync(source.Id);
        stressNew += report.Created;
    }
}

sw.Stop();

var goldItems = golden.ItemsOf(setName).ToList();
var predicted = new List<WorkItem>();
foreach (var projectId in projectIds)
    predicted.AddRange(await store.GetItemsAsync(projectId));

// Утечка контекста: сущность проекта ссылается на сообщение другого проекта.
var messageProjects = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var projectId in projectIds)
foreach (var source in await store.GetSourcesAsync(projectId))
foreach (var message in await store.GetMessagesAsync(source.Id))
    messageProjects[message.Id] = message.ProjectId;

var leaks = predicted.Count(item => item.Evidence.Any(e =>
    messageProjects.TryGetValue(e.MessageId, out var owner) && owner != item.ProjectId));

var embeddings = sp.GetRequiredService<IEmbeddingClient>();
var llm = sp.GetRequiredService<ILlmClient>();

var matcher = new Matcher(embeddings.Dimensions);
var match = matcher.Match(goldItems, predicted);

var evalReport = MetricsCalculator.Build(setName, match, goldItems, predicted, stressNew, leaks);
evalReport.ElapsedMs = sw.ElapsedMilliseconds;
evalReport.LlmProvider = llm.Name;
evalReport.EmbeddingProvider = embeddings.Name;

ReportWriter.PrintToConsole(evalReport);
var path = ReportWriter.Write(evalReport);
Console.WriteLine($"Отчёт: {path}");

// Ненулевой код возврата, если провалена хотя бы одна бинарная метрика —
// прогон можно поставить в CI и в pre-commit.
var hardFailures = evalReport.Metrics
    .Where(m => m.LowerIsBetter && m.Target == 0 && m.Ok == false)
    .ToList();

if (hardFailures.Count > 0)
{
    Console.WriteLine();
    foreach (var f in hardFailures)
        Console.WriteLine($"ПРОВАЛ: {f.Name} = {f.Display}, требуется 0");
    return 2;
}

return 0;

string? GetArg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

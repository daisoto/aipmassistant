using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pm.Application;
using Pm.Application.Pipeline;
using Pm.Application.PostMeeting;
using Pm.Application.Views;
using Pm.Infrastructure;
using Pm.Infrastructure.Data;
using Pm.Infrastructure.Materials;

// Headless-интерфейс к тому же пайплайну, что и в вебе: сторонний сервис вызывает
// команды через Process и разбирает код возврата (0 — успех, 1 — ошибка).
//
//   dotnet run --project src/Pm.Cli -- ingest ./inbox/urbankey-call.json
//   dotnet run --project src/Pm.Cli -- run urbankey
//   dotnet run --project src/Pm.Cli -- export urbankey --out ./out
//   dotnet run --project src/Pm.Cli -- postmeeting urbankey.call --out ./out
//   dotnet run --project src/Pm.Cli -- llm-log --last 20

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
var positional = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

var configuration = new ConfigurationBuilder()
    .SetBasePath(RepoPaths.Root)
    // Оба файла необязательны: под PM_ROOT вне репозитория их нет, и тогда всё
    // задаётся переменными окружения (Llm__Provider, ConnectionStrings__Postgres и т. д.).
    .AddJsonFile(Path.Combine("src", "Pm.Web", "appsettings.json"), optional: true)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddPmAssistant(configuration);
await using var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();
var sp = scope.ServiceProvider;
var store = sp.GetRequiredService<IPmStore>();

try
{
    await DbInitializer.InitializeAsync(
        sp.GetRequiredService<PmDbContext>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pm.Cli"));

    return command switch
    {
        "ingest" => await IngestAsync(),
        "run" => await RunAsync(),
        "export" => await ExportAsync(),
        "postmeeting" => await PostMeetingAsync(),
        "llm-log" => await LlmLogAsync(),
        _ => Fail($"Неизвестная команда «{command}». Список: --help")
    };
}
catch (Exception ex)
{
    return Fail(ex.Message);
}

async Task<int> IngestAsync()
{
    // Без аргумента — папка materials/ рядом с корнем, то есть прежнее поведение кнопки в вебе.
    var path = positional.FirstOrDefault();
    var loaded = await sp.GetRequiredService<MaterialLoader>().LoadAllAsync(path);
    Console.WriteLine($"Загружено источников: {loaded}");
    return loaded > 0 ? 0 : Fail("Ни одного источника не загружено.");
}

async Task<int> RunAsync()
{
    var pipeline = sp.GetRequiredService<PipelineRunner>();

    var projectIds = Flag("--all") is not null || positional.Length == 0
        ? (await store.GetProjectsAsync()).Select(p => p.Id).ToList()
        : [.. positional];

    if (projectIds.Count == 0) return Fail("Проектов нет. Сначала выполните ingest.");

    var failed = false;
    foreach (var projectId in projectIds)
    {
        if (await store.GetProjectAsync(projectId) is null)
        {
            Console.Error.WriteLine($"Проект {projectId} не найден.");
            failed = true;
            continue;
        }

        foreach (var report in await pipeline.RunProjectAsync(projectId))
        {
            Console.WriteLine(
                $"{projectId}/{report.SourceTitle,-28} кандидатов {report.CandidateCount,3}  " +
                $"новых {report.Created,3}  обновлений {report.Updated,3}  замен {report.Superseded,2}  " +
                $"шум {report.Noise,3}  [{report.CorrelationId}]");
        }
    }

    return failed ? 1 : 0;
}

async Task<int> ExportAsync()
{
    if (positional.FirstOrDefault() is not { } projectId)
        return Fail("Укажите идентификатор проекта: export <projectId> [--out <папка>]");

    var exporter = sp.GetRequiredService<Exporter>();
    var dir = OutDir();

    var json = Path.Combine(dir, $"{projectId}-tracker.json");
    var markdown = Path.Combine(dir, $"{projectId}-tracker.md");
    await File.WriteAllTextAsync(json, await exporter.TrackerJsonAsync(projectId));
    await File.WriteAllTextAsync(markdown, await exporter.TrackerMarkdownAsync(projectId));

    Console.WriteLine(json);
    Console.WriteLine(markdown);
    return 0;
}

async Task<int> PostMeetingAsync()
{
    if (positional.FirstOrDefault() is not { } sourceId)
        return Fail("Укажите идентификатор источника: postmeeting <sourceId> [--out <папка>]");

    var source = await store.GetSourceAsync(sourceId) ?? throw new InvalidOperationException($"Источник {sourceId} не найден");

    // Если документа ещё нет — собираем его здесь же, иначе CLI требовал бы зайти в веб.
    var exporter = sp.GetRequiredService<Exporter>();
    var existing = (await store.GetPostMeetingsAsync(source.ProjectId)).Any(d => d.SourceId == sourceId);
    if (!existing)
        await sp.GetRequiredService<PostMeetingComposer>().ComposeAsync(sourceId);

    var path = Path.Combine(OutDir(), $"{sourceId}-postmeeting.md");
    await File.WriteAllTextAsync(path, await exporter.PostMeetingAsync(sourceId));
    Console.WriteLine(path);
    return 0;
}

async Task<int> LlmLogAsync()
{
    var limit = int.TryParse(Flag("--last"), out var n) ? n : 20;
    var correlation = Flag("--correlation");

    var calls = await store.GetLlmCallsAsync(correlation is null ? limit : 1000);
    if (correlation is not null)
        calls = [.. calls.Where(c => c.CorrelationId == correlation).Take(limit)];

    if (calls.Count == 0)
    {
        Console.WriteLine("Журнал пуст.");
        return 0;
    }

    foreach (var call in calls.Reverse())
    {
        var tokens = call.InputTokens is null
            ? $"{call.PromptChars,6} симв."
            : $"{call.InputTokens,6}→{call.OutputTokens,-6} ток.";

        Console.WriteLine(
            $"{call.At:dd.MM HH:mm:ss}  {call.Operation,-10} {call.SchemaMode,-11} попытка {call.Attempt}  " +
            $"{tokens}  {call.ElapsedMs,6} мс  {(call.Failed ? "ОШИБКА" : "ок")}  [{call.CorrelationId}]");
        if (call.Error is not null) Console.WriteLine($"    {call.Error}");
        if (call.ResponseJson is not null) Console.WriteLine($"    ответ: {Trim(call.ResponseJson)}");
    }

    return 0;
}

string OutDir()
{
    var dir = Path.GetFullPath(Flag("--out") ?? ".");
    Directory.CreateDirectory(dir);
    return dir;
}

string? Flag(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    var value = index + 1 < args.Length ? args[index + 1] : null;
    // Флаг без значения (--all) отдаёт пустую строку, чтобы отличаться от отсутствующего.
    return value is null || value.StartsWith("--", StringComparison.Ordinal) ? "" : value;
}

static string Trim(string text)
    => text.Length <= 300 ? text.ReplaceLineEndings(" ") : text.ReplaceLineEndings(" ")[..300] + "…";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void PrintUsage() => Console.WriteLine(
    """
    Консольный интерфейс AI-ассистента PM.

      ingest [путь]                   загрузить материалы (файл или папка; по умолчанию materials/)
      run [projectId...] | --all      прогнать необработанные источники через пайплайн
      export <projectId> [--out dir]  выгрузить трекер в JSON и Markdown
      postmeeting <sourceId> [--out]  сформировать (если нужно) и выгрузить post-meeting
      llm-log [--last N] [--correlation id]   показать журнал обращений к модели

    Корень с materials/, golden/ и reports/ задаётся переменной PM_ROOT.
    Код возврата: 0 — успех, 1 — ошибка.
    """);

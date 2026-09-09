using Pm.Application;
using Pm.Domain;

namespace Pm.Tests;

/// <summary>
/// Хранилище в памяти. Реализованы только методы, которые дёргают резолвер и загрузчик
/// материалов; остальное бросает — если тест туда попал, он проверяет не то, что заявлено.
/// </summary>
public sealed class FakeStore : IPmStore
{
    public List<Project> Projects { get; } = [];
    public List<Source> Sources { get; } = [];
    public List<Message> Messages { get; } = [];
    public List<WorkItem> Items { get; } = [];
    public List<LlmCall> Calls { get; } = [];

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Project>>(Projects);

    public Task<Project?> GetProjectAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Projects.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<Source>> GetSourcesAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Source>>(Sources.Where(s => s.ProjectId == projectId).ToList());

    public Task<Source?> GetSourceAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Sources.FirstOrDefault(s => s.Id == id));

    public Task<IReadOnlyList<Message>> GetMessagesAsync(string sourceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Message>>(Messages.Where(m => m.SourceId == sourceId).ToList());

    public Task<IReadOnlyList<Message>> GetMessagesByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Message>>(Messages.Where(m => ids.Contains(m.Id)).ToList());

    public Task<IReadOnlyList<WorkItem>> GetItemsAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkItem>>(Items.Where(i => i.ProjectId == projectId).ToList());

    public Task<WorkItem?> GetItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

    public Task UpsertProjectAsync(Project project, CancellationToken ct = default)
    {
        Projects.RemoveAll(p => p.Id == project.Id);
        Projects.Add(project);
        return Task.CompletedTask;
    }

    public Task AddSourceAsync(Source source, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        Sources.RemoveAll(s => s.Id == source.Id);
        Sources.Add(source);
        Messages.RemoveAll(m => m.SourceId == source.Id);
        Messages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task AddItemAsync(WorkItem item, CancellationToken ct = default)
    {
        Items.Add(item);
        return Task.CompletedTask;
    }

    public Task UpdateItemAsync(WorkItem item, CancellationToken ct = default)
    {
        Items.RemoveAll(i => i.Id == item.Id);
        Items.Add(item);
        return Task.CompletedTask;
    }

    public Task AddLlmCallAsync(LlmCall call, CancellationToken ct = default)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LlmCall>> GetLlmCallsAsync(int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LlmCall>>(Calls.TakeLast(limit).ToList());

    public Task<IReadOnlyList<PostMeetingDoc>> GetPostMeetingsAsync(string projectId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task AddPostMeetingAsync(PostMeetingDoc doc, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task MarkSourceIngestedAsync(string sourceId, CancellationToken ct = default)
    {
        var source = Sources.FirstOrDefault(s => s.Id == sourceId);
        if (source is not null) source.Ingested = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task RunInTransactionAsync(Func<CancellationToken, Task> body, CancellationToken ct = default)
        => body(ct);
}

/// <summary>Индекс кандидатов с заранее заданным ответом: близость задаёт тест, а не эмбеддинги.</summary>
public sealed class FakeIndex(params ScoredItem[] result) : ICandidateIndex
{
    public Task<IReadOnlyList<ScoredItem>> FindAsync(
        string projectId, string title, string body, float[] embedding, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScoredItem>>(result);
}

/// <summary>Модель с заранее заданным решением — проверяется реакция резолвера, а не сама модель.</summary>
public sealed class FakeLlm(ResolveDecision decision) : ILlmClient
{
    public string Name => "fake";
    public int ResolveCalls { get; private set; }

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default)
        => Task.FromResult(new ExtractionResult());

    public Task<ResolveDecision> ResolveAsync(ResolveRequest request, CancellationToken ct = default)
    {
        ResolveCalls++;
        return Task.FromResult(decision);
    }

    public Task<IReadOnlyList<PostMeetingSection>> PolishAsync(
        IReadOnlyList<PostMeetingSection> draft, CancellationToken ct = default)
        => Task.FromResult(draft);
}

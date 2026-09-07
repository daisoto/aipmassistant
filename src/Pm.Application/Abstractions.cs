using Pm.Domain;

namespace Pm.Application;

/// <summary>
/// Языковая модель за интерфейсом. Требование кейса — работать в закрытом контуре,
/// поэтому смена провайдера не должна затрагивать пайплайн: меняется только реализация.
/// </summary>
public interface ILlmClient
{
    string Name { get; }

    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default);

    Task<ResolveDecision> ResolveAsync(ResolveRequest request, CancellationToken ct = default);

    /// <summary>
    /// Полировка формулировок post-meeting. Модели разрешено менять слова и запрещено
    /// добавлять содержание; проверяет это PhrasingGuard, а не доверие к промпту.
    /// </summary>
    Task<IReadOnlyList<PostMeetingSection>> PolishAsync(
        IReadOnlyList<PostMeetingSection> draft, CancellationToken ct = default);
}

public interface IEmbeddingClient
{
    string Name { get; }

    /// <summary>Размерность вектора. Должна совпадать с типом колонки vector(N) в БД.</summary>
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Кандидаты на слияние: гибрид векторной близости и совпадения ключевых слов.
/// Ищет только внутри проекта — здесь же обеспечивается изоляция контекста.
/// </summary>
public interface ICandidateIndex
{
    Task<IReadOnlyList<ScoredItem>> FindAsync(
        string projectId, string title, string body, float[] embedding, int limit, CancellationToken ct = default);
}

/// <summary>Доступ к состоянию. Реализация — EF Core поверх PostgreSQL.</summary>
public interface IPmStore
{
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default);
    Task<Project?> GetProjectAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Source>> GetSourcesAsync(string projectId, CancellationToken ct = default);
    Task<Source?> GetSourceAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Message>> GetMessagesAsync(string sourceId, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetMessagesByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default);

    Task<IReadOnlyList<WorkItem>> GetItemsAsync(string projectId, CancellationToken ct = default);
    Task<WorkItem?> GetItemAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<PostMeetingDoc>> GetPostMeetingsAsync(string projectId, CancellationToken ct = default);

    Task UpsertProjectAsync(Project project, CancellationToken ct = default);
    Task AddSourceAsync(Source source, IReadOnlyList<Message> messages, CancellationToken ct = default);
    Task AddItemAsync(WorkItem item, CancellationToken ct = default);
    Task UpdateItemAsync(WorkItem item, CancellationToken ct = default);
    Task AddPostMeetingAsync(PostMeetingDoc doc, CancellationToken ct = default);
    Task AddLlmCallAsync(LlmCall call, CancellationToken ct = default);
    Task MarkSourceIngestedAsync(string sourceId, CancellationToken ct = default);

    Task<IReadOnlyList<LlmCall>> GetLlmCallsAsync(int limit, CancellationToken ct = default);

    /// <summary>Полная очистка состояния. Нужна прогонщику метрик и кнопке «Загрузить заново».</summary>
    Task ResetAsync(CancellationToken ct = default);
}

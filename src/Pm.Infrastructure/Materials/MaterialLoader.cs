using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pm.Application;
using Pm.Domain;

namespace Pm.Infrastructure.Materials;

public sealed class MaterialFile
{
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectDescription { get; set; } = "";
    public string SourceId { get; set; } = "";
    public SourceKind Kind { get; set; }
    public string Title { get; set; } = "";
    public DateOnly OccurredOn { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// Признак стресс-теста: сообщения принадлежат разным проектам и перемешаны.
    /// Загрузчик разложит их по отдельным источникам — по одному на проект.
    /// </summary>
    public bool SplitByProject { get; set; }

    public List<MaterialMessage> Messages { get; set; } = [];
}

public sealed class MaterialMessage
{
    public string Id { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? At { get; set; }
    public string Author { get; set; } = "";
    public Side Side { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Читает materials/*/*.json и кладёт проекты, источники и сообщения в хранилище.</summary>
public sealed class MaterialLoader(IPmStore store, ILogger<MaterialLoader> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Читает папку с материалами. Путь параметризован: без него материалы можно взять
    /// только из репозитория, а вводить данные извне — единственный способ добавить проект.
    /// Принимает и папку, и отдельный файл.
    /// </summary>
    public async Task<int> LoadAllAsync(string? path = null, CancellationToken ct = default)
    {
        path ??= RepoPaths.Materials;

        List<string> files;
        if (File.Exists(path))
        {
            files = [path];
        }
        else if (Directory.Exists(path))
        {
            files = Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            throw new DirectoryNotFoundException($"Не найдены материалы: {path}");
        }

        var loaded = 0;
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            loaded += await LoadStreamAsync(stream, file, ct);
        }

        logger.LogInformation("Загружено источников: {Count}", loaded);
        return loaded;
    }

    /// <summary>
    /// Разбор одного файла из потока — вход для загрузки через браузер, где файла на диске нет.
    /// Имя нужно только для сообщения об ошибке.
    /// </summary>
    public async Task<int> LoadStreamAsync(Stream stream, string name, CancellationToken ct = default)
    {
        MaterialFile? file;
        try
        {
            file = await JsonSerializer.DeserializeAsync<MaterialFile>(stream, Json, ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name}: не разбирается как материал — {ex.Message}", ex);
        }

        if (file is null)
            throw new InvalidDataException($"{name}: пустой файл.");

        // Валидация на границе доверия: без projectId и сообщений источник осядет в базе
        // как пустой мусор, найти который потом можно только руками в SQL.
        if (string.IsNullOrWhiteSpace(file.ProjectId))
            throw new InvalidDataException($"{name}: не задан projectId.");
        if (string.IsNullOrWhiteSpace(file.SourceId))
            throw new InvalidDataException($"{name}: не задан sourceId.");
        if (file.Messages.Count == 0)
            throw new InvalidDataException($"{name}: в файле нет сообщений.");
        if (file.Messages.Any(m => string.IsNullOrWhiteSpace(m.Id)))
            throw new InvalidDataException($"{name}: у сообщения нет id.");

        return await LoadFileAsync(file, ct);
    }

    private async Task<int> LoadFileAsync(MaterialFile file, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(file.ProjectId))
        {
            await store.UpsertProjectAsync(new Project
            {
                Id = file.ProjectId,
                Name = string.IsNullOrWhiteSpace(file.ProjectName) ? file.ProjectId : file.ProjectName,
                Description = file.ProjectDescription
            }, ct);
        }

        if (!file.SplitByProject)
        {
            await AddSourceAsync(file, file.ProjectId, file.SourceId, file.Title, file.Messages, ct);
            return 1;
        }

        // Стресс-тест: один файл → несколько источников, по одному на проект.
        var groups = file.Messages
            .GroupBy(m => m.ProjectId ?? file.ProjectId)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToList();

        foreach (var group in groups)
        {
            await AddSourceAsync(
                file,
                group.Key!,
                $"{file.SourceId}.{group.Key}",
                file.Title,
                group.ToList(),
                ct);
        }

        return groups.Count;
    }

    private async Task AddSourceAsync(
        MaterialFile file, string projectId, string sourceId, string title,
        IReadOnlyList<MaterialMessage> messages, CancellationToken ct)
    {
        var source = new Source
        {
            Id = sourceId,
            ProjectId = projectId,
            Kind = file.Kind,
            Title = title,
            OccurredOn = file.OccurredOn,
            Order = file.Order
        };

        var mapped = messages.Select((m, i) => new Message
        {
            Id = m.Id,
            SourceId = sourceId,
            ProjectId = projectId,
            Order = i,
            At = m.At,
            AuthorName = m.Author,
            AuthorSide = m.Side,
            Text = m.Text
        }).ToList();

        await store.AddSourceAsync(source, mapped, ct);
    }
}

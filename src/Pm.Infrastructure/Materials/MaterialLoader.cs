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

    public async Task<int> LoadAllAsync(CancellationToken ct = default)
    {
        var root = RepoPaths.Materials;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Не найдена папка с материалами: {root}");

        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var loaded = 0;
        foreach (var path in files)
        {
            var file = JsonSerializer.Deserialize<MaterialFile>(await File.ReadAllTextAsync(path, ct), Json);
            if (file is null)
            {
                logger.LogWarning("Не удалось разобрать {Path}", path);
                continue;
            }

            loaded += await LoadFileAsync(file, ct);
        }

        logger.LogInformation("Загружено источников: {Count}", loaded);
        return loaded;
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

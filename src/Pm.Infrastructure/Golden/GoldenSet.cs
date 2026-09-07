using System.Text.Json;
using System.Text.Json.Serialization;
using Pm.Domain;

namespace Pm.Infrastructure.Golden;

/// <summary>
/// Эталонная сущность: то, что сервис обязан достать из материалов.
/// Размечается руками — заказчик обещанный набор ожидаемых задач не прислал.
/// </summary>
public sealed class GoldenItem
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public ItemKind Kind { get; set; }
    public string Title { get; set; } = "";
    public Side Side { get; set; }
    public string? Assignee { get; set; }

    /// <summary>Ожидаемый срок в ISO. null — срока в коммуникации нет, и его нельзя выдумывать.</summary>
    public string? Due { get; set; }

    public string? DueQuote { get; set; }
    public DueKind DueKind { get; set; } = DueKind.None;
    public bool IsPromiseToClient { get; set; }

    /// <summary>Синонимы заголовка: сопоставление предсказания с эталоном нечёткое.</summary>
    public List<string> Aliases { get; set; } = [];

    public List<string> Evidence { get; set; } = [];

    /// <summary>Метки разобранных ловушек — по ним прогонщик считает покрытие фикстур.</summary>
    public List<string> Traps { get; set; } = [];
}

public sealed class GoldenProject
{
    public string ProjectId { get; set; } = "";
    public string Set { get; set; } = "dev";
    public List<GoldenItem> Items { get; set; } = [];
}

/// <summary>Ожидаемый результат стресс-теста: ноль новых сущностей и ноль утечек между проектами.</summary>
public sealed class GoldenStress
{
    public int ExpectedNewItems { get; set; }
    public List<string> ExpectedUpdatedTitles { get; set; } = [];
    public List<string> SourceIds { get; set; } = [];
}

public sealed class GoldenSet
{
    public List<GoldenProject> Projects { get; set; } = [];
    public GoldenStress Stress { get; set; } = new();
    public Dictionary<string, string> PostMeetings { get; set; } = [];

    public IEnumerable<GoldenItem> ItemsOf(string setName)
        => Projects.Where(p => string.Equals(p.Set, setName, StringComparison.OrdinalIgnoreCase))
                   .SelectMany(p => p.Items);

    public IEnumerable<string> ProjectsOf(string setName)
        => Projects.Where(p => string.Equals(p.Set, setName, StringComparison.OrdinalIgnoreCase))
                   .Select(p => p.ProjectId);
}

public static class GoldenLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<GoldenSet> LoadAsync(CancellationToken ct = default)
    {
        var root = RepoPaths.Golden;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Не найдена папка с эталоном: {root}");

        var set = new GoldenSet();

        foreach (var path in Directory.EnumerateFiles(root, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var text = await File.ReadAllTextAsync(path, ct);

            if (string.Equals(name, "stress", StringComparison.OrdinalIgnoreCase))
            {
                set.Stress = JsonSerializer.Deserialize<GoldenStress>(text, Json) ?? new GoldenStress();
                continue;
            }

            var project = JsonSerializer.Deserialize<GoldenProject>(text, Json);
            if (project is null) continue;
            foreach (var item in project.Items) item.ProjectId = project.ProjectId;
            set.Projects.Add(project);
        }

        var pmDir = Path.Combine(root, "postmeeting");
        if (Directory.Exists(pmDir))
        {
            foreach (var path in Directory.EnumerateFiles(pmDir, "*.md"))
                set.PostMeetings[Path.GetFileNameWithoutExtension(path)] = await File.ReadAllTextAsync(path, ct);
        }

        return set;
    }
}

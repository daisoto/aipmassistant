namespace Pm.Infrastructure;

/// <summary>
/// Корень, рядом с которым лежат materials, golden и reports.
///
/// Приоритет у переменной окружения PM_ROOT: без неё опубликованное приложение
/// (dotnet publish вне checkout) не находит .sln и падает на старте. Поиск вверх
/// по дереву остаётся запасным путём — из репозитория всё работает как раньше.
/// </summary>
public static class RepoPaths
{
    public const string RootVariable = "PM_ROOT";

    private static string? _root;

    public static string Root => _root ??= FindRoot();

    public static string Materials => Path.Combine(Root, "materials");
    public static string Golden => Path.Combine(Root, "golden");
    public static string Reports => Path.Combine(Root, "reports");

    private static string FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException($"{RootVariable} указывает на несуществующую папку: {full}");
            return full;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AIPMAssistant.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AIPMAssistant.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Не найден корень решения (AIPMAssistant.sln). Запускайте из папки репозитория " +
            $"или задайте переменную окружения {RootVariable}.");
    }
}

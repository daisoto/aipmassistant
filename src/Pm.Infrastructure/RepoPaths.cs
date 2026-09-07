namespace Pm.Infrastructure;

/// <summary>
/// Поиск корня репозитория. Materials, golden и reports лежат рядом с решением,
/// а рабочая директория у Pm.Web и Pm.Eval разная, поэтому путь ищем вверх по дереву.
/// </summary>
public static class RepoPaths
{
    private static string? _root;

    public static string Root => _root ??= FindRoot();

    public static string Materials => Path.Combine(Root, "materials");
    public static string Golden => Path.Combine(Root, "golden");
    public static string Reports => Path.Combine(Root, "reports");

    private static string FindRoot()
    {
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
            "Не найден корень решения (AIPMAssistant.sln). Запускайте из папки репозитория.");
    }
}

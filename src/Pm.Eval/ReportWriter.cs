using System.Globalization;
using System.Text;
using Pm.Infrastructure;

namespace Pm.Eval;

/// <summary>
/// Пишет отчёт в reports/ и печатает таблицу в консоль. Отдельным блоком — дельта
/// к предыдущему прогону: правка промпта имеет нелокальные последствия, и без сравнения
/// с прошлым запуском не видно, что починив одно, сломали другое.
/// </summary>
public static class ReportWriter
{
    public static string Write(EvalReport report)
    {
        Directory.CreateDirectory(RepoPaths.Reports);
        var previous = FindPrevious(report.SetName);

        var sb = new StringBuilder();
        sb.AppendLine($"# Прогон метрик — набор `{report.SetName}`");
        sb.AppendLine();
        sb.AppendLine($"- Дата: {report.CreatedAt:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"- Провайдер LLM: `{report.LlmProvider}`");
        sb.AppendLine($"- Эмбеддинги: `{report.EmbeddingProvider}`");
        sb.AppendLine($"- Время прогона: {report.ElapsedMs} мс");
        sb.AppendLine();

        sb.AppendLine("## Метрики");
        sb.AppendLine();
        sb.AppendLine("| Метрика | Значение | Цель | Статус | Дельта |");
        sb.AppendLine("|---|---:|---:|:--:|---:|");
        foreach (var m in report.Metrics)
        {
            var status = m.Ok switch { true => "OK", false => "ниже цели", _ => "—" };
            var target = m.Target?.ToString(m.Format, CultureInfo.InvariantCulture) ?? "—";
            sb.AppendLine($"| {m.Name} | {m.Display} | {target} | {status} | {Delta(previous, m)} |");
        }

        sb.AppendLine();

        if (report.HallucinatedDeadlines.Count > 0)
        {
            sb.AppendLine("## Выдуманные сроки");
            sb.AppendLine();
            foreach (var line in report.HallucinatedDeadlines) sb.AppendLine($"- {line}");
            sb.AppendLine();
        }

        if (report.TrapResults.Count > 0)
        {
            sb.AppendLine("## Покрытие ловушек");
            sb.AppendLine();
            foreach (var line in report.TrapResults) sb.AppendLine($"- {line}");
            sb.AppendLine();
        }

        if (report.Missed.Count > 0)
        {
            sb.AppendLine("## Не найдено (эталон есть, предсказания нет)");
            sb.AppendLine();
            foreach (var g in report.Missed) sb.AppendLine($"- [{g.ProjectId}] {g.Title}");
            sb.AppendLine();
        }

        if (report.Extra.Count > 0)
        {
            sb.AppendLine("## Лишнее (предсказание есть, эталона нет)");
            sb.AppendLine();
            foreach (var p in report.Extra) sb.AppendLine($"- [{p.ProjectId}] {p.Title}");
            sb.AppendLine();
        }

        if (report.Disputed.Count > 0)
        {
            sb.AppendLine("## Спорные пары — просмотреть глазами");
            sb.AppendLine();
            foreach (var d in report.Disputed)
                sb.AppendLine($"- {d.Score:F2} · эталон «{d.Gold.Title}» ↔ предсказание «{d.Predicted?.Title}»");
            sb.AppendLine();
        }

        var path = Path.Combine(
            RepoPaths.Reports,
            $"eval-{report.SetName}-{report.CreatedAt:yyyy-MM-dd-HHmm}.md");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    public static void PrintToConsole(EvalReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"Набор: {report.SetName}   LLM: {report.LlmProvider}   эмбеддинги: {report.EmbeddingProvider}");
        Console.WriteLine(new string('-', 74));
        Console.WriteLine($"{"Метрика",-40}{"Значение",12}{"Цель",10}{"Статус",12}");
        Console.WriteLine(new string('-', 74));
        foreach (var m in report.Metrics)
        {
            var target = m.Target?.ToString(m.Format, CultureInfo.InvariantCulture) ?? "—";
            var status = m.Ok switch { true => "OK", false => "ниже цели", _ => "—" };
            Console.WriteLine($"{m.Name,-40}{m.Display,12}{target,10}{status,12}");
        }

        Console.WriteLine(new string('-', 74));
        Console.WriteLine($"Не найдено: {report.Missed.Count}   лишнее: {report.Extra.Count}   спорных: {report.Disputed.Count}");
    }

    private static string Delta(EvalReport? previous, MetricValue current)
    {
        var before = previous?.Metrics.FirstOrDefault(m => m.Name == current.Name);
        if (before is null) return "—";
        var delta = current.Value - before.Value;
        if (Math.Abs(delta) < 1e-9) return "0";
        return delta > 0
            ? "+" + delta.ToString(current.Format, CultureInfo.InvariantCulture)
            : delta.ToString(current.Format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Предыдущий отчёт читается из уже записанного markdown — так дельта переживает
    /// перезапуск процесса и остаётся видна в git-истории отчётов.
    /// </summary>
    private static EvalReport? FindPrevious(string setName)
    {
        if (!Directory.Exists(RepoPaths.Reports)) return null;

        var file = Directory.EnumerateFiles(RepoPaths.Reports, $"eval-{setName}-*.md")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        if (file is null) return null;

        var report = new EvalReport { SetName = setName };
        foreach (var line in File.ReadLines(file))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 4) continue;
            if (!double.TryParse(cells[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) continue;
            report.Metrics.Add(new MetricValue(cells[1], value, "F3", null));
        }

        return report.Metrics.Count > 0 ? report : null;
    }
}

using System.Globalization;
using Pm.Domain;
using Pm.Infrastructure.Golden;

namespace Pm.Eval;

public sealed class MetricValue(string name, double value, string format, double? target, bool lowerIsBetter = false)
{
    public string Name { get; } = name;
    public double Value { get; } = value;
    public string Format { get; } = format;
    public double? Target { get; } = target;
    public bool LowerIsBetter { get; } = lowerIsBetter;

    public bool? Ok => Target is null
        ? null
        : LowerIsBetter ? Value <= Target : Value >= Target;

    public string Display => Value.ToString(Format, CultureInfo.InvariantCulture);
}

public sealed class EvalReport
{
    public string SetName { get; set; } = "";
    public string LlmProvider { get; set; } = "";
    public string EmbeddingProvider { get; set; } = "";
    public long ElapsedMs { get; set; }
    public List<MetricValue> Metrics { get; set; } = [];
    public List<GoldenItem> Missed { get; set; } = [];
    public List<WorkItem> Extra { get; set; } = [];
    public List<MatchPair> Disputed { get; set; } = [];
    public List<string> HallucinatedDeadlines { get; set; } = [];
    public List<string> TrapResults { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public static class MetricsCalculator
{
    public static EvalReport Build(
        string setName,
        Matcher.Result match,
        IReadOnlyList<GoldenItem> gold,
        IReadOnlyList<WorkItem> predicted,
        int stressNewItems,
        int crossProjectLeaks)
    {
        var report = new EvalReport { SetName = setName };

        var recall = gold.Count == 0 ? 0 : (double)match.Matched.Count / gold.Count;
        var precision = predicted.Count == 0 ? 0 : (double)match.Matched.Count / predicted.Count;

        var hallucinated = match.Matched
            .Where(p => p.Gold.Due is null && p.Predicted!.DueKind == DueKind.Explicit)
            .ToList();

        var missedDeadlines = match.Matched
            .Count(p => p.Gold.Due is not null && p.Predicted!.DueAt is null);

        var withBothDeadlines = match.Matched
            .Where(p => p.Gold.Due is not null && p.Predicted!.DueAt is not null)
            .ToList();

        var deadlineAccuracy = withBothDeadlines.Count == 0
            ? 1.0
            : (double)withBothDeadlines.Count(p => SameDate(p.Gold.Due!, p.Predicted!.DueAt!.Value))
              / withBothDeadlines.Count;

        var sideAccuracy = match.Matched.Count == 0
            ? 0
            : (double)match.Matched.Count(p => p.Gold.Side == p.Predicted!.Side) / match.Matched.Count;

        report.Metrics =
        [
            new MetricValue("Recall по сущностям", recall, "F3", 0.90),
            new MetricValue("Precision по сущностям", precision, "F3", 0.85),
            new MetricValue("Выдуманные сроки", hallucinated.Count, "F0", 0, lowerIsBetter: true),
            new MetricValue("Пропущенные сроки", missedDeadlines, "F0", 1, lowerIsBetter: true),
            new MetricValue("Точность сроков", deadlineAccuracy, "F3", 0.90),
            new MetricValue("Точность стороны", sideAccuracy, "F3", 0.90),
            new MetricValue("Новых сущностей на стресс-тесте", stressNewItems, "F0", 0, lowerIsBetter: true),
            new MetricValue("Утечки между проектами", crossProjectLeaks, "F0", 0, lowerIsBetter: true)
        ];

        report.Missed = match.Missed;
        report.Extra = match.Extra;
        report.Disputed = match.Disputed;
        report.HallucinatedDeadlines = hallucinated
            .Select(p => $"«{p.Predicted!.Title}» получил срок {p.Predicted.DueQuote}, хотя в эталоне срока нет")
            .ToList();

        report.TrapResults = BuildTrapResults(gold, match);

        return report;
    }

    /// <summary>Покрытие разобранных ловушек: слияние, отмена требования, срок через подтверждение и т.д.</summary>
    private static List<string> BuildTrapResults(IReadOnlyList<GoldenItem> gold, Matcher.Result match)
    {
        var caught = match.Matched.Select(m => m.Gold.Id).ToHashSet(StringComparer.Ordinal);
        return gold
            .SelectMany(g => g.Traps.Select(t => (Trap: t, Caught: caught.Contains(g.Id))))
            .GroupBy(x => x.Trap)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count(x => x.Caught)}/{g.Count()}")
            .ToList();
    }

    private static bool SameDate(string goldIso, DateTimeOffset predicted)
        => DateTimeOffset.TryParse(goldIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var g)
           && g.UtcDateTime.Date == predicted.UtcDateTime.Date;
}

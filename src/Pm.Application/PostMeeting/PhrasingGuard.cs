using System.Text.RegularExpressions;
using Pm.Domain;

namespace Pm.Application.PostMeeting;

public sealed record PhrasingGuardResult(bool Ok, IReadOnlyList<string> Violations);

/// <summary>
/// Модели разрешено менять слова и запрещено добавлять содержание.
/// Проверяем это, а не полагаемся на формулировку промпта:
///   1. состав и количество буллетов по секциям не изменились;
///   2. множество дат, чисел и времени на выходе ⊆ множества на входе;
///   3. множество имён собственных на выходе ⊆ множества на входе.
/// При нарушении композитор откатывается к неотполированной версии.
/// </summary>
public sealed partial class PhrasingGuard
{
    public PhrasingGuardResult Check(
        IReadOnlyList<PostMeetingSection> original,
        IReadOnlyList<PostMeetingSection> polished)
    {
        var violations = new List<string>();

        if (original.Count != polished.Count)
            violations.Add($"Изменилось количество секций: {original.Count} → {polished.Count}");

        var byTitle = polished.ToDictionary(s => s.Title, StringComparer.Ordinal);

        foreach (var section in original)
        {
            if (!byTitle.TryGetValue(section.Title, out var got))
            {
                violations.Add($"Пропала секция «{section.Title}»");
                continue;
            }

            if (got.Bullets.Count != section.Bullets.Count)
                violations.Add(
                    $"Секция «{section.Title}»: было {section.Bullets.Count} пунктов, стало {got.Bullets.Count}");
        }

        var srcNumbers = Numbers(original);
        foreach (var n in Numbers(polished).Except(srcNumbers))
            violations.Add($"В тексте появилось число, которого не было в исходных данных: {n}");

        var srcNames = ProperNames(original);
        foreach (var name in ProperNames(polished).Except(srcNames))
            violations.Add($"В тексте появилось имя, которого не было в исходных данных: {name}");

        return new PhrasingGuardResult(violations.Count == 0, violations);
    }

    private static HashSet<string> Numbers(IEnumerable<PostMeetingSection> sections)
        => sections
            .SelectMany(s => s.Bullets)
            .SelectMany(b => NumberRegex().Matches(b).Select(m => m.Value))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Слово с заглавной буквы не в начале пункта. Грубо, но достаточно, чтобы поймать
    /// подставленное имя ответственного или название подрядчика.
    /// </summary>
    private static HashSet<string> ProperNames(IEnumerable<PostMeetingSection> sections)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bullet in sections.SelectMany(s => s.Bullets))
        {
            var separators = new[] { ' ', ',', ';', '.', '(', ')' };
            var words = bullet.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < words.Length; i++)
            {
                var w = words[i];
                if (w.Length > 1 && char.IsUpper(w[0]) && !w.All(char.IsUpper))
                    names.Add(w);
            }
        }

        return names;
    }

    [GeneratedRegex(@"\d+(?:[:.,]\d+)?")]
    private static partial Regex NumberRegex();
}

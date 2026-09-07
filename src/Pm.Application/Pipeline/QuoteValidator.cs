using Pm.Application.Text;
using Pm.Domain;

namespace Pm.Application.Pipeline;

public sealed record QuoteValidationReport(int DeadlinesDropped, int AssigneesDropped, List<string> Log);

/// <summary>
/// Детерминированный слой между моделью и базой.
///
/// Правило кейса «сервис не придумывает дедлайн» реализовано здесь, а не в промпте:
/// если дословной цитаты нет в тексте указанного сообщения — атрибут обнуляется.
/// Метрика «выдуманных сроков = 0» получается по построению, а не по удаче с формулировкой промпта.
/// </summary>
public sealed class QuoteValidator
{
    public QuoteValidationReport Validate(ExtractionResult result, IReadOnlyList<Message> block)
    {
        var byId = block.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var log = new List<string>();
        var deadlinesDropped = 0;
        var assigneesDropped = 0;

        foreach (var c in result.Candidates)
        {
            c.EvidenceMessageIds = c.EvidenceMessageIds
                .Where(byId.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (c.Deadline is { } d && !IsGrounded(byId, d))
            {
                log.Add($"Срок «{d.Quote}» отброшен: цитаты нет в {d.SourceMessageId}. Кандидат: {c.Title}");
                c.Deadline = null;
                deadlinesDropped++;
            }

            if (c.Assignee is { } a && !IsGrounded(byId, a))
            {
                log.Add($"Исполнитель «{a.Value}» отброшен: цитаты нет в {a.SourceMessageId}. Кандидат: {c.Title}");
                c.Assignee = null;
                assigneesDropped++;
            }
        }

        result.Candidates.RemoveAll(c => string.IsNullOrWhiteSpace(c.Title) || c.EvidenceMessageIds.Count == 0);

        return new QuoteValidationReport(deadlinesDropped, assigneesDropped, log);
    }

    private static bool IsGrounded(IReadOnlyDictionary<string, Message> byId, Attributed<string> attributed)
    {
        if (string.IsNullOrWhiteSpace(attributed.Quote)) return false;

        if (byId.TryGetValue(attributed.SourceMessageId, out var exact)
            && TextUtil.ContainsQuote(exact.Text, attributed.Quote))
            return true;

        // Модель могла ошибиться идентификатором сообщения. Цитата всё ещё легитимна,
        // если дословно встречается в любом сообщении блока: срок реально произнесён,
        // а мы боремся с выдуманными сроками, а не с промахами по ссылке.
        return byId.Values.Any(m => TextUtil.ContainsQuote(m.Text, attributed.Quote));
    }
}

using System.Text;
using Pm.Domain;

namespace Pm.Application;

/// <summary>
/// Промпты и JSON-схемы. Вынесены отдельно: это тот код, который правится чаще всего,
/// и каждая правка должна прогоняться через Pm.Eval.
/// </summary>
public static class Prompts
{
    public const string ExtractionSystem = """
        Ты — часть внутреннего сервиса проектного менеджера IT-компании. Ты разбираешь блок
        проектной коммуникации и возвращаешь структурированный список сущностей.

        ТИПЫ СУЩНОСТЕЙ:
        - Task — требуется конкретное действие («проверим и вернёмся», «пришлю баннер»).
        - Requirement — пожелание к продукту («хотим фильтр по сроку сдачи»).
        - Decision — принятое решение или ограничение («самовывоз все регионы, курьер только Москва»).
        - OpenQuestion — вопрос без принятого решения («канал уведомлений не выбрали»).
        - Dependency — ожидание от третьей стороны («интегратор пришлёт спецификацию»).

        СТОРОНА ОТВЕТСТВЕННОСТИ определяется по тому, КТО ВЫПОЛНЯЕТ, а не кто попросил:
        Xpage — команда исполнителя; Client — клиент; Contractor — подрядчик или интегратор.

        ЖЁСТКИЕ ПРАВИЛА:
        1. Срок указывай ТОЛЬКО дословной цитатой из текста сообщения (поле deadlineQuote).
           Никогда не вычисляй дату и не подставляй срок, которого нет. Нет цитаты — null.
        2. Исполнителя указывай, только если он назван в тексте. Иначе null.
        3. Несколько сообщений об одном и том же — ОДИН кандидат с несколькими evidenceMessageIds.
        4. isPromiseToClient = true, если это обещание вернуться к клиенту с ответом
           («вернёмся», «ответим», «сообщим», «подготовим оценку»). Внутренняя задача команде — false.
        5. Реплики без действия («Ок», «Спасибо», «Принято») — в noiseMessageIds.
           НО «Да», «Подходит», «Всё верно» после вопроса — НЕ шум: это подтверждение решения или срока,
           и оно должно попасть в evidenceMessageIds соответствующего кандидата.
        6. Заголовок (title) — короткая формулировка действия в инфинитиве или назывном виде,
           до 90 символов, без воды.

        Ответ — строго JSON по заданной схеме, без пояснений вокруг.
        """;

    public const string ResolveSystem = """
        Ты сопоставляешь новый кандидат с уже известными сущностями проекта и выбираешь ОДНУ операцию:

        - New       — в списке нет того же самого; это новая сущность.
        - Update    — то же самое, но добавились детали: срок, исполнитель, уточнение формулировки.
        - Supersede — требование ЗАМЕНЕНО новым, противоречащим прежнему
                      (например, «убрать курьерскую доставку» → «оставить курьера для Москвы»).
        - Close     — договорённость выполнена или снята.
        - Noise     — действия не требует.

        ПРАВИЛА:
        1. Сомневаешься между Update и New — выбирай New. Потерянная договорённость хуже дубля.
        2. Supersede только при прямом противоречии прежней формулировке, а не при уточнении.
        3. targetId обязателен для Update, Supersede и Close и должен быть из предложенного списка.
        4. В patch клади только то, что реально изменилось. Даты не вычисляй: deadlineQuote — цитата.

        Ответ — строго JSON по заданной схеме.
        """;

    public const string PolishSystem = """
        Ты редактируешь формулировки пунктов post-meeting. Тебе разрешено ТОЛЬКО переписать
        текст пункта в деловом стиле.

        ЗАПРЕЩЕНО: добавлять или удалять пункты, менять порядок секций, добавлять даты, числа,
        имена, сроки и ответственных, которых нет во входных данных.

        Сохрани количество пунктов в каждой секции в точности. Ответ — строго JSON по схеме.
        """;

    public static string ExtractionUser(ExtractionRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Проект: {r.Project.Name}. {r.Project.Description}");
        sb.AppendLine($"Источник: {SourceLabel(r.Source.Kind)} «{r.Source.Title}», дата {r.Source.OccurredOn:dd.MM.yyyy}.");
        sb.AppendLine();

        if (r.KnownItems.Count > 0)
        {
            sb.AppendLine("Уже известные сущности проекта (для контекста, не дублируй их без нужды):");
            foreach (var item in r.KnownItems.Take(30))
                sb.AppendLine($"- [{item.Kind}/{item.Side}] {item.Title}");
            sb.AppendLine();
        }

        sb.AppendLine("Сообщения блока:");
        foreach (var m in r.Block)
            sb.AppendLine($"[{m.Id}] {m.At} {m.AuthorName} ({m.AuthorSide}): {m.Text}");

        return sb.ToString();
    }

    public static string ResolveUser(ResolveRequest r)
    {
        var sb = new StringBuilder();
        var c = r.Candidate;
        sb.AppendLine("НОВЫЙ КАНДИДАТ:");
        sb.AppendLine($"  тип: {c.Kind}, сторона: {c.Side}");
        sb.AppendLine($"  заголовок: {c.Title}");
        if (!string.IsNullOrWhiteSpace(c.Body)) sb.AppendLine($"  детали: {c.Body}");
        if (c.Deadline is not null) sb.AppendLine($"  срок (цитата): {c.Deadline.Quote}");
        if (c.Assignee is not null) sb.AppendLine($"  исполнитель: {c.Assignee.Value}");
        sb.AppendLine();

        sb.AppendLine("ИЗВЕСТНЫЕ СУЩНОСТИ ПРОЕКТА:");
        foreach (var n in r.Nearest)
        {
            sb.AppendLine($"- id={n.Item.Id} близость={n.Score:F2} [{n.Item.Kind}/{n.Item.Side}/{n.Item.Status}] {n.Item.Title}");
            if (!string.IsNullOrWhiteSpace(n.Item.DueQuote)) sb.AppendLine($"    срок: {n.Item.DueQuote}");
            foreach (var rev in n.Item.Revisions.TakeLast(3))
                sb.AppendLine($"    история: {rev.Op} — {rev.Summary}");
        }

        sb.AppendLine();
        sb.AppendLine("КОНТЕКСТ ПЕРЕПИСКИ:");
        foreach (var m in r.Context.Where(m => c.EvidenceMessageIds.Contains(m.Id)))
            sb.AppendLine($"[{m.Id}] {m.AuthorName}: {m.Text}");

        return sb.ToString();
    }

    public static string SourceLabel(SourceKind kind) => kind switch
    {
        SourceKind.ClientChat => "клиентский чат",
        SourceKind.InternalChat => "внутренний чат команды",
        SourceKind.Email => "письмо",
        SourceKind.CallTranscript => "транскрипт созвона",
        _ => kind.ToString()
    };

    /// <summary>Схема ответа извлечения. Уходит в response_format: json_schema со strict: true.</summary>
    public const string ExtractionSchema = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["candidates", "noiseMessageIds"],
      "properties": {
        "candidates": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["tempId","kind","title","body","side","assignee","assigneeQuote",
                         "deadlineQuote","deadlineMessageId","isPromiseToClient","evidenceMessageIds","rationale"],
            "properties": {
              "tempId": { "type": "string" },
              "kind": { "type": "string", "enum": ["Task","Decision","Requirement","OpenQuestion","Dependency"] },
              "title": { "type": "string" },
              "body": { "type": "string" },
              "side": { "type": "string", "enum": ["Xpage","Client","Contractor"] },
              "assignee": { "type": ["string","null"] },
              "assigneeQuote": { "type": ["string","null"] },
              "deadlineQuote": { "type": ["string","null"] },
              "deadlineMessageId": { "type": ["string","null"] },
              "isPromiseToClient": { "type": "boolean" },
              "evidenceMessageIds": { "type": "array", "items": { "type": "string" } },
              "rationale": { "type": "string" }
            }
          }
        },
        "noiseMessageIds": { "type": "array", "items": { "type": "string" } }
      }
    }
    """;

    public const string ResolveSchema = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["op","targetId","patch","reason","confidence"],
      "properties": {
        "op": { "type": "string", "enum": ["New","Update","Supersede","Close","Noise"] },
        "targetId": { "type": ["string","null"] },
        "patch": {
          "type": ["object","null"],
          "additionalProperties": false,
          "required": ["title","body","side","kind","assignee","deadlineQuote","deadlineMessageId","isPromiseToClient","status"],
          "properties": {
            "title": { "type": ["string","null"] },
            "body": { "type": ["string","null"] },
            "side": { "type": ["string","null"], "enum": ["Xpage","Client","Contractor",null] },
            "kind": { "type": ["string","null"], "enum": ["Task","Decision","Requirement","OpenQuestion","Dependency",null] },
            "assignee": { "type": ["string","null"] },
            "deadlineQuote": { "type": ["string","null"] },
            "deadlineMessageId": { "type": ["string","null"] },
            "isPromiseToClient": { "type": ["boolean","null"] },
            "status": { "type": ["string","null"], "enum": ["Open","Done","Cancelled","Superseded",null] }
          }
        },
        "reason": { "type": "string" },
        "confidence": { "type": "number" }
      }
    }
    """;

    public const string PolishSchema = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["sections"],
      "properties": {
        "sections": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["title","bullets"],
            "properties": {
              "title": { "type": "string" },
              "bullets": { "type": "array", "items": { "type": "string" } }
            }
          }
        }
      }
    }
    """;
}

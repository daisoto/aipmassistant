using System.Globalization;
using System.Text;
using Pm.Domain;

namespace Pm.Application.PostMeeting;

/// <summary>
/// Рендер в формат, принятый в Xpage. Шаблон восстановлен из присланного примера:
/// шапка с датой, пять секций, буллеты «●», ответственный инлайн через тире,
/// срок человеческим текстом, а не в ISO.
/// </summary>
public sealed class PostMeetingRenderer
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public string Bullet(WorkItem item, bool includeAssigneeAndDue)
    {
        var sb = new StringBuilder(item.Title.TrimEnd('.', ' '));

        if (includeAssigneeAndDue)
        {
            if (!string.IsNullOrWhiteSpace(item.Assignee))
                sb.Append(" — ").Append(item.Assignee);

            // Срок не дублируется, если он уже прозвучал в самой формулировке пункта.
            if (item.DueKind == DueKind.Explicit
                && !string.IsNullOrWhiteSpace(item.DueQuote)
                && !item.Title.Contains(item.DueQuote, StringComparison.OrdinalIgnoreCase))
            {
                var quote = item.DueQuote.Trim();
                sb.Append(string.IsNullOrWhiteSpace(item.Assignee) ? " — " : ", ");
                sb.Append(quote.StartsWith("до ", StringComparison.OrdinalIgnoreCase)
                          || quote.StartsWith("к ", StringComparison.OrdinalIgnoreCase)
                          || quote.StartsWith("в течение", StringComparison.OrdinalIgnoreCase)
                    ? quote
                    : "до " + quote);
            }
        }

        return sb.ToString();
    }

    public string Render(PostMeetingDoc doc)
    {
        var sb = new StringBuilder();
        sb.Append("Коллеги, фиксирую итоги и договоренности по встрече ")
          .Append(doc.MeetingDate.ToString("dd.MM.yyyy", Ru))
          .AppendLine(":");
        sb.AppendLine();

        foreach (var section in doc.Sections)
        {
            if (section.Bullets.Count == 0) continue;
            sb.AppendLine(section.Title);
            sb.AppendLine();
            foreach (var bullet in section.Bullets)
                sb.Append("● ").AppendLine(bullet);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}

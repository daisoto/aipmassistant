using Pm.Domain;

namespace Pm.Application.Views;

/// <summary>
/// Пять срезов трекера, которых требует кейс. Выводятся правилами, без обращения к модели.
/// </summary>
public static class TrackerViews
{
    private static readonly string[] TeamRoles =
        ["разработчик", "аналитик", "дизайнер", "тестировщик", "команда", "devops"];

    public static string Title(TrackerView view) => view switch
    {
        TrackerView.DoMyself => "Сделать самому",
        TrackerView.DelegateToTeam => "Передать команде",
        TrackerView.AskClient => "Спросить у клиента",
        TrackerView.WatchContractor => "Проконтролировать подрядчика",
        TrackerView.GetBackToClient => "Вернуться с обратной связью",
        _ => view.ToString()
    };

    public static IReadOnlyList<TrackerView> All =>
    [
        TrackerView.DoMyself,
        TrackerView.DelegateToTeam,
        TrackerView.AskClient,
        TrackerView.WatchContractor,
        TrackerView.GetBackToClient
    ];

    public static bool Matches(WorkItem item, TrackerView view)
    {
        if (item.Status is ItemStatus.Superseded or ItemStatus.Cancelled) return false;

        return view switch
        {
            // Обещание клиенту выделяется в свой срез раньше остальных правил:
            // именно оно отделяет внешний срок («вернёмся завтра до 15:00»)
            // от внутренней задачи разработчику («ответ нужен завтра до 13:00»).
            TrackerView.GetBackToClient =>
                item.Side == Side.Xpage && item.IsPromiseToClient,

            TrackerView.DelegateToTeam =>
                item.Side == Side.Xpage && !item.IsPromiseToClient && IsTeamRole(item.Assignee),

            TrackerView.DoMyself =>
                item.Side == Side.Xpage && !item.IsPromiseToClient && !IsTeamRole(item.Assignee)
                && item.Kind is ItemKind.Task or ItemKind.Requirement,

            TrackerView.AskClient =>
                item.Side == Side.Client && item.Kind is ItemKind.Task or ItemKind.OpenQuestion,

            TrackerView.WatchContractor =>
                item.Side == Side.Contractor || item.Kind == ItemKind.Dependency,

            _ => false
        };
    }

    public static IReadOnlyList<WorkItem> Filter(IEnumerable<WorkItem> items, TrackerView view)
        => items.Where(i => Matches(i, view))
                .OrderBy(i => i.DueAt ?? DateTimeOffset.MaxValue)
                .ThenBy(i => i.CreatedAt)
                .ToList();

    /// <summary>Сущности, не попавшие ни в один срез: решения, зафиксированные требования, закрытое.</summary>
    public static IReadOnlyList<WorkItem> Uncategorized(IEnumerable<WorkItem> items)
        => items.Where(i => All.All(v => !Matches(i, v))).OrderBy(i => i.CreatedAt).ToList();

    private static bool IsTeamRole(string? assignee)
    {
        if (string.IsNullOrWhiteSpace(assignee)) return false;
        var a = assignee.ToLowerInvariant();
        return TeamRoles.Any(r => a.Contains(r, StringComparison.Ordinal));
    }
}

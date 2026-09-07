using Pm.Application.PostMeeting;
using Pm.Domain;
using Xunit;

namespace Pm.Tests;

public class PhrasingGuardTests
{
    private readonly PhrasingGuard _guard = new();

    private static List<PostMeetingSection> Draft() =>
    [
        new() { Title = "С нашей стороны", Bullets = ["Проверить интеграцию по API — Алексей", "Обновить макеты"] },
        new() { Title = "Требует уточнения", Bullets = ["Не определён канал уведомлений"] }
    ];

    [Fact]
    public void Rewording_is_allowed()
    {
        var polished = new List<PostMeetingSection>
        {
            new() { Title = "С нашей стороны", Bullets = ["Проверить интеграцию по API — Алексей", "Обновить макеты сайта"] },
            new() { Title = "Требует уточнения", Bullets = ["Канал уведомлений не определён"] }
        };

        Assert.True(_guard.Check(Draft(), polished).Ok);
    }

    [Fact]
    public void Added_bullet_is_rejected()
    {
        var polished = Draft();
        polished[0].Bullets.Add("Согласовать бюджет");

        var result = _guard.Check(Draft(), polished);

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Contains("пунктов"));
    }

    [Fact]
    public void Invented_date_is_rejected()
    {
        var polished = Draft();
        polished[0].Bullets[1] = "Обновить макеты до 12 сентября";

        var result = _guard.Check(Draft(), polished);

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Contains("число"));
    }

    [Fact]
    public void Invented_name_is_rejected()
    {
        var polished = Draft();
        polished[0].Bullets[1] = "Обновить макеты — Мария";

        var result = _guard.Check(Draft(), polished);

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Contains("имя"));
    }

    [Fact]
    public void Missing_section_is_rejected()
    {
        var polished = new List<PostMeetingSection> { Draft()[0] };

        Assert.False(_guard.Check(Draft(), polished).Ok);
    }
}

public class PostMeetingRendererTests
{
    private readonly PostMeetingRenderer _renderer = new();

    [Fact]
    public void Bullet_omits_assignee_and_due_when_absent()
    {
        var item = new WorkItem { Title = "Обновить макеты после получения финальных текстов" };
        Assert.Equal("Обновить макеты после получения финальных текстов",
            _renderer.Bullet(item, includeAssigneeAndDue: true));
    }

    [Fact]
    public void Bullet_renders_assignee_and_due_inline()
    {
        var item = new WorkItem
        {
            Title = "Согласовать обновлённые макеты",
            Assignee = "Анна",
            DueKind = DueKind.Explicit,
            DueQuote = "12 сентября"
        };

        Assert.Equal("Согласовать обновлённые макеты — Анна, до 12 сентября",
            _renderer.Bullet(item, includeAssigneeAndDue: true));
    }

    [Fact]
    public void Bullet_keeps_existing_preposition()
    {
        var item = new WorkItem
        {
            Title = "Передать API-документацию",
            DueKind = DueKind.Explicit,
            DueQuote = "до конца недели"
        };

        Assert.Equal("Передать API-документацию — до конца недели",
            _renderer.Bullet(item, includeAssigneeAndDue: true));
    }

    [Fact]
    public void Vague_due_is_not_printed()
    {
        var item = new WorkItem
        {
            Title = "Учесть роль администратора",
            DueKind = DueKind.Vague,
            DueQuote = "срок не критичный"
        };

        Assert.Equal("Учесть роль администратора", _renderer.Bullet(item, includeAssigneeAndDue: true));
    }

    [Fact]
    public void Document_uses_xpage_header_and_skips_empty_sections()
    {
        var doc = new PostMeetingDoc
        {
            MeetingDate = new DateOnly(2026, 9, 8),
            Sections =
            [
                new PostMeetingSection { Title = "Ключевые итоги", Bullets = ["Согласовали состав релиза"] },
                new PostMeetingSection { Title = "С вашей стороны", Bullets = [] }
            ]
        };

        var text = _renderer.Render(doc);

        Assert.StartsWith("Коллеги, фиксирую итоги и договоренности по встрече 08.09.2026:", text);
        Assert.Contains("● Согласовали состав релиза", text);
        Assert.DoesNotContain("С вашей стороны", text);
    }
}

using Pm.Application.Text;
using Pm.Application.Views;
using Pm.Domain;
using Xunit;

namespace Pm.Tests;

public class TrackerViewsTests
{
    /// <summary>
    /// Ловушка из материалов: одно требование по видео порождает две задачи —
    /// обещание клиенту «вернёмся завтра до 15:00» и внутреннюю разработчику «до 13:00».
    /// Они обязаны попасть в разные срезы.
    /// </summary>
    [Fact]
    public void Promise_to_client_and_internal_task_land_in_different_views()
    {
        var promise = new WorkItem
        {
            Title = "Проверить технические ограничения по видео и вернуться с ответом",
            Side = Side.Xpage, Kind = ItemKind.Task, IsPromiseToClient = true
        };

        var internalTask = new WorkItem
        {
            Title = "Проверить ограничения хранения и загрузки видео",
            Side = Side.Xpage, Kind = ItemKind.Task, Assignee = "Разработчик"
        };

        Assert.True(TrackerViews.Matches(promise, TrackerView.GetBackToClient));
        Assert.False(TrackerViews.Matches(promise, TrackerView.DelegateToTeam));

        Assert.True(TrackerViews.Matches(internalTask, TrackerView.DelegateToTeam));
        Assert.False(TrackerViews.Matches(internalTask, TrackerView.GetBackToClient));
    }

    [Fact]
    public void Client_open_question_goes_to_ask_client()
    {
        var item = new WorkItem
        {
            Title = "Нужна ли история заявок в личном кабинете",
            Side = Side.Client, Kind = ItemKind.OpenQuestion
        };

        Assert.True(TrackerViews.Matches(item, TrackerView.AskClient));
    }

    [Fact]
    public void Dependency_goes_to_contractor_view()
    {
        var item = new WorkItem
        {
            Title = "Спецификация MES API от интегратора",
            Side = Side.Contractor, Kind = ItemKind.Dependency
        };

        Assert.True(TrackerViews.Matches(item, TrackerView.WatchContractor));
    }

    [Fact]
    public void Superseded_item_disappears_from_every_view()
    {
        var item = new WorkItem
        {
            Title = "Убрать курьерскую доставку",
            Side = Side.Xpage, Kind = ItemKind.Requirement, Status = ItemStatus.Superseded
        };

        Assert.All(TrackerViews.All, view => Assert.False(TrackerViews.Matches(item, view)));
    }
}

public class TextUtilTests
{
    [Fact]
    public void Quote_matching_ignores_case_quotes_and_yo()
    {
        Assert.True(TextUtil.ContainsQuote("Вернёмся Завтра До 15:00.", "вернемся завтра до 15:00"));
    }

    [Fact]
    public void Quote_matching_rejects_absent_text()
    {
        Assert.False(TextUtil.ContainsQuote("Баннер пришлю позже.", "до пятницы"));
    }

    [Fact]
    public void Stemming_brings_word_forms_together()
    {
        Assert.Equal(TextUtil.Stem("фильтра"), TextUtil.Stem("фильтры"));
    }

    [Fact]
    public void Cosine_of_identical_vectors_is_one()
    {
        float[] v = [0.1f, 0.5f, 0.9f];
        Assert.Equal(1.0, TextUtil.Cosine(v, v), 5);
    }
}

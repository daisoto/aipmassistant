using Pm.Application;
using Pm.Application.Pipeline;
using Pm.Domain;
using Xunit;

namespace Pm.Tests;

/// <summary>
/// Запрет «не придумывать дедлайн» проверяется здесь: срок без дословной цитаты
/// в тексте сообщения обнуляется независимо от того, что вернула модель.
/// </summary>
public class QuoteValidatorTests
{
    private readonly QuoteValidator _validator = new();

    private static Message Msg(string id, string text) => new()
    {
        Id = id, SourceId = "s", ProjectId = "p", AuthorName = "Клиент",
        AuthorSide = Side.Client, Text = text
    };

    private static Candidate Candidate(string title, Attributed<string>? deadline, params string[] evidence) => new()
    {
        Title = title,
        Body = title,
        EvidenceMessageIds = [.. evidence],
        Deadline = deadline
    };

    [Fact]
    public void Deadline_present_in_message_survives()
    {
        var block = new List<Message> { Msg("m1", "Баннер пришлю завтра до 12:00.") };
        var result = new ExtractionResult
        {
            Candidates = [Candidate("Прислать баннер", new Attributed<string>("завтра до 12:00", "завтра до 12:00", "m1"), "m1")]
        };

        var report = _validator.Validate(result, block);

        Assert.Equal(0, report.DeadlinesDropped);
        Assert.NotNull(result.Candidates[0].Deadline);
    }

    [Fact]
    public void Invented_deadline_is_dropped()
    {
        var block = new List<Message> { Msg("m1", "Баннер для главной пришлю позже.") };
        var result = new ExtractionResult
        {
            Candidates = [Candidate("Прислать баннер", new Attributed<string>("до пятницы", "до пятницы", "m1"), "m1")]
        };

        var report = _validator.Validate(result, block);

        Assert.Equal(1, report.DeadlinesDropped);
        Assert.Null(result.Candidates[0].Deadline);
    }

    [Fact]
    public void Quote_found_in_another_message_of_the_block_is_accepted()
    {
        var block = new List<Message>
        {
            Msg("m1", "Баннер пришлю."),
            Msg("m2", "Завтра до 12:00.")
        };
        var result = new ExtractionResult
        {
            Candidates = [Candidate("Прислать баннер", new Attributed<string>("завтра до 12:00", "завтра до 12:00", "m1"), "m1")]
        };

        var report = _validator.Validate(result, block);

        Assert.Equal(0, report.DeadlinesDropped);
    }

    [Fact]
    public void Evidence_outside_the_block_is_stripped()
    {
        var block = new List<Message> { Msg("m1", "Нужен фильтр по сроку сдачи.") };
        var result = new ExtractionResult
        {
            Candidates = [Candidate("Фильтр по сроку сдачи", null, "m1", "m99")]
        };

        _validator.Validate(result, block);

        Assert.Equal(new[] { "m1" }, result.Candidates[0].EvidenceMessageIds);
    }

    [Fact]
    public void Candidate_without_evidence_is_discarded()
    {
        var block = new List<Message> { Msg("m1", "Текст.") };
        var result = new ExtractionResult { Candidates = [Candidate("Выдуманная задача", null, "m42")] };

        _validator.Validate(result, block);

        Assert.Empty(result.Candidates);
    }
}

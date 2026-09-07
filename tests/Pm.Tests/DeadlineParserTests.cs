using Pm.Application.Deadlines;
using Pm.Domain;
using Xunit;

namespace Pm.Tests;

/// <summary>
/// Парсер сроков — самое дешёвое место, где можно получить твёрдую уверенность.
/// Все девять правил из плана покрыты, включая главное: формулировка, которая
/// упоминает срок, но срока не задаёт, датой не становится.
/// </summary>
public class DeadlineParserTests
{
    private readonly DeadlineParser _parser = new();

    // Вторник — дата всех источников в materials/.
    private static readonly DateOnly Tuesday = new(2026, 9, 8);

    [Fact]
    public void Today_resolves_to_source_date()
    {
        var result = _parser.Parse("сегодня", Tuesday);
        Assert.Equal(DueKind.Explicit, result.Kind);
        Assert.Equal(new DateOnly(2026, 9, 8), DateOnly.FromDateTime(result.At!.Value.DateTime));
    }

    [Fact]
    public void Tomorrow_with_time_resolves_to_next_day_at_that_time()
    {
        var result = _parser.Parse("завтра до 15:00", Tuesday);
        Assert.Equal("TomorrowAtTime", result.RuleName);
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 15, 0, 0, DeadlineParser.Offset), result.At);
    }

    [Fact]
    public void Tomorrow_end_of_day_uses_business_day_end()
    {
        var result = _parser.Parse("завтра до конца дня", Tuesday);
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 18, 0, 0, DeadlineParser.Offset), result.At);
    }

    [Fact]
    public void Weekday_resolves_to_next_occurrence()
    {
        // Сказано во вторник «до среды» — значит завтра.
        var result = _parser.Parse("до среды", Tuesday);
        Assert.Equal(new DateOnly(2026, 9, 9), DateOnly.FromDateTime(result.At!.Value.DateTime));
    }

    [Fact]
    public void Thursday_resolves_two_days_ahead()
    {
        var result = _parser.Parse("до четверга", Tuesday);
        Assert.Equal(new DateOnly(2026, 9, 10), DateOnly.FromDateTime(result.At!.Value.DateTime));
    }

    [Fact]
    public void End_of_week_resolves_to_friday()
    {
        var result = _parser.Parse("до конца недели", Tuesday);
        Assert.Equal(new DateOnly(2026, 9, 11), DateOnly.FromDateTime(result.At!.Value.DateTime));
    }

    [Fact]
    public void Explicit_date_is_parsed()
    {
        var result = _parser.Parse("14 сентября", Tuesday);
        Assert.Equal("ExplicitDate", result.RuleName);
        Assert.Equal(new DateOnly(2026, 9, 14), DateOnly.FromDateTime(result.At!.Value.DateTime));
    }

    [Fact]
    public void Explicit_date_with_time_is_parsed()
    {
        var result = _parser.Parse("до 16:00 10 сентября", Tuesday);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 16, 0, 0, DeadlineParser.Offset), result.At);
    }

    [Fact]
    public void Business_days_skip_weekend()
    {
        // Пятница + 2 рабочих дня = вторник.
        var friday = new DateOnly(2026, 9, 11);
        var result = _parser.Parse("в течение 2 рабочих дней", friday);
        Assert.Equal(new DateOnly(2026, 9, 15), DateOnly.FromDateTime(result.At!.Value.DateTime));
    }

    [Theory]
    [InlineData("срок не критичный")]
    [InlineData("в следующем плановом цикле")]
    [InlineData("в следующем спринте")]
    [InlineData("позже")]
    public void Vague_wording_never_becomes_a_date(string quote)
    {
        var result = _parser.Parse(quote, Tuesday);
        Assert.Null(result.At);
        Assert.Equal(DueKind.Vague, result.Kind);
    }

    [Fact]
    public void Empty_quote_gives_no_deadline()
    {
        var result = _parser.Parse(null, Tuesday);
        Assert.Null(result.At);
        Assert.Equal(DueKind.None, result.Kind);
    }

    [Fact]
    public void Unknown_wording_gives_no_deadline_instead_of_guessing()
    {
        var result = _parser.Parse("когда-нибудь потом", Tuesday);
        Assert.Null(result.At);
        Assert.Equal(DueKind.None, result.Kind);
    }
}

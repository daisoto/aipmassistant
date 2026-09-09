using Microsoft.Extensions.Logging.Abstractions;
using Pm.Application;
using Pm.Application.Deadlines;
using Pm.Application.Pipeline;
using Pm.Domain;
using Xunit;

namespace Pm.Tests;

/// <summary>
/// Резолвер решает, дублировать сущность или слить, и на этом держится весь трекер.
/// Проверяются четыре решения, ошибка в каждом из которых видна только на прогоне целиком:
/// быстрый путь, guard от переслияния, неизвестная цель и подмена срока при Update.
/// </summary>
public class ResolverTests
{
    private static readonly Project Project = new() { Id = "p", Name = "Проект" };

    private static readonly Source Source = new()
    {
        Id = "s", ProjectId = "p", Kind = SourceKind.ClientChat,
        Title = "Чат", OccurredOn = new DateOnly(2026, 9, 8)
    };

    private static Candidate Candidate(string title, string? deadlineQuote = null) => new()
    {
        Kind = ItemKind.Task,
        Title = title,
        Body = title,
        Side = Side.Xpage,
        EvidenceMessageIds = ["m1"],
        Deadline = deadlineQuote is null ? null : new Attributed<string>(deadlineQuote, deadlineQuote, "m1")
    };

    private static WorkItem Existing(string title, string id = "existing") => new()
    {
        Id = id, ProjectId = "p", Kind = ItemKind.Task, Title = title, Body = title, Side = Side.Xpage
    };

    private static (Resolver Resolver, FakeStore Store, FakeLlm Llm) Build(
        ResolveDecision decision, params ScoredItem[] nearest)
    {
        var store = new FakeStore();
        var llm = new FakeLlm(decision);
        var resolver = new Resolver(
            new FakeIndex(nearest), llm, new DeadlineParser(), store,
            NullLogger<Resolver>.Instance);
        return (resolver, store, llm);
    }

    private static ResolveDecision New() => new(ResolveOp.New, null, null, "", 1);

    [Fact]
    public async Task Score_below_threshold_creates_item_without_calling_the_model()
    {
        // 0.30 < FastPathThreshold 0.55 — вызов модели здесь только тратил бы токены.
        var (resolver, store, llm) = Build(New(), new ScoredItem(Existing("Другое"), 0.30, 0.30, 0.30));

        var outcome = await resolver.ResolveAsync(Project, Source, Candidate("Новая задача"), [], []);

        Assert.Equal(ResolveOp.New, outcome.Op);
        Assert.Equal(0, llm.ResolveCalls);
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task Empty_index_creates_a_new_item()
    {
        var (resolver, store, llm) = Build(New());

        var outcome = await resolver.ResolveAsync(Project, Source, Candidate("Первая задача"), [], []);

        Assert.Equal(ResolveOp.New, outcome.Op);
        Assert.Equal(0, llm.ResolveCalls);
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task Guard_rejects_merge_with_a_weakly_similar_item()
    {
        // Лучший кандидат 0.56 проходит быстрый путь и вызывает модель, но слить она
        // предлагает со вторым — 0.40, ниже MergeGuardThreshold 0.45. Потерянная
        // договорённость хуже дубля, поэтому ожидается New, а не Update.
        var strong = new ScoredItem(Existing("Похожая сильно"), 0.56, 0.56, 0.56);
        var weak = new ScoredItem(Existing("Похожая слабо", "weak"), 0.40, 0.40, 0.40);
        var (resolver, store, _) = Build(
            new ResolveDecision(ResolveOp.Update, "weak", null, "", 0.9), strong, weak);

        var outcome = await resolver.ResolveAsync(Project, Source, Candidate("Кандидат"), [], []);

        Assert.Equal(ResolveOp.New, outcome.Op);
        Assert.True(outcome.GuardTriggered);
        Assert.Single(store.Items);
        Assert.Contains("Guard", outcome.Reason);
    }

    [Fact]
    public async Task Unknown_merge_target_creates_a_new_item()
    {
        // Модель может назвать targetId, которого нет в предложенном списке.
        // Молча потерять кандидата здесь означало бы потерять договорённость.
        var (resolver, store, _) = Build(
            new ResolveDecision(ResolveOp.Update, "нет-такого", null, "", 0.9),
            new ScoredItem(Existing("Похожая"), 0.80, 0.80, 0.80));

        var outcome = await resolver.ResolveAsync(Project, Source, Candidate("Кандидат"), [], []);

        Assert.Equal(ResolveOp.New, outcome.Op);
        Assert.True(outcome.GuardTriggered);
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task Update_takes_deadline_from_quote_and_dates_it_from_the_source()
    {
        var (resolver, store, _) = Build(
            new ResolveDecision(ResolveOp.Update, "existing", null, "уточнение", 0.9),
            new ScoredItem(Existing("Прислать макет"), 0.80, 0.80, 0.80));

        var outcome = await resolver.ResolveAsync(
            Project, Source, Candidate("Прислать макет", "завтра до 15:00"), [], []);

        Assert.Equal(ResolveOp.Update, outcome.Op);
        var item = Assert.Single(store.Items);
        Assert.Equal(DueKind.Explicit, item.DueKind);
        Assert.Equal("завтра до 15:00", item.DueQuote);
        // Дата источника 08.09.2026, значит «завтра» — девятое, а не сегодняшний день.
        Assert.Equal(new DateOnly(2026, 9, 9), DateOnly.FromDateTime(item.DueAt!.Value.LocalDateTime));
    }

    [Fact]
    public async Task Supersede_marks_the_old_item_and_links_it_to_the_replacement()
    {
        var old = Existing("Курьерская доставка во все регионы");
        var (resolver, store, _) = Build(
            new ResolveDecision(ResolveOp.Supersede, "existing", null, "прямое противоречие", 0.9),
            new ScoredItem(old, 0.80, 0.80, 0.80));

        var outcome = await resolver.ResolveAsync(
            Project, Source, Candidate("Курьер только для Москвы"), [], []);

        Assert.Equal(ResolveOp.Supersede, outcome.Op);
        Assert.Equal(2, store.Items.Count);

        var superseded = store.Items.Single(i => i.Id == "existing");
        var replacement = store.Items.Single(i => i.Id != "existing");
        Assert.Equal(ItemStatus.Superseded, superseded.Status);
        Assert.Equal(replacement.Id, superseded.SupersededById);
        // История — это и есть объяснимость: обе стороны замены должны её нести.
        Assert.Contains(superseded.Revisions, r => r.Op == ResolveOp.Supersede);
        Assert.Contains(replacement.Revisions, r => r.Op == ResolveOp.Supersede);
    }

    [Fact]
    public async Task Noise_writes_nothing_to_the_store()
    {
        var (resolver, store, _) = Build(
            new ResolveDecision(ResolveOp.Noise, null, null, "действия не требует", 0.9),
            new ScoredItem(Existing("Похожая"), 0.80, 0.80, 0.80));

        var outcome = await resolver.ResolveAsync(Project, Source, Candidate("Ок, принято"), [], []);

        Assert.Equal(ResolveOp.Noise, outcome.Op);
        Assert.Empty(store.Items);
    }
}

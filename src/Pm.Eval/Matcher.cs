using Pm.Application.Text;
using Pm.Infrastructure.Embeddings;
using Pm.Domain;
using Pm.Infrastructure.Golden;

namespace Pm.Eval;

public sealed record MatchPair(GoldenItem Gold, WorkItem? Predicted, double Score);

/// <summary>
/// Сопоставление предсказанного с эталонным. Сравнение нечёткое: модель напишет
/// «Добавить фильтр по сроку сдачи в форму подбора», в эталоне — «Фильтр по сроку сдачи квартиры».
/// Это одно и то же, но Assert.Equal такого не увидит.
///
///   score = 0.6 · близость заголовков + 0.4 · Жаккар по сообщениям-источникам
///           (+0.15, если заголовок содержит один из alias эталона)
///
/// Жадное сопоставление по убыванию, каждая эталонная сущность матчится не более одного раза.
/// </summary>
public sealed class Matcher(int embeddingDimensions)
{
    public const double MatchThreshold = 0.60;
    public const double DisputedLow = 0.50;
    public const double DisputedHigh = 0.65;

    public sealed record Result(
        List<MatchPair> Matched,
        List<GoldenItem> Missed,
        List<WorkItem> Extra,
        List<MatchPair> Disputed);

    public Result Match(IReadOnlyList<GoldenItem> gold, IReadOnlyList<WorkItem> predicted)
    {
        var pairs = new List<(GoldenItem Gold, WorkItem Item, double Score)>();

        foreach (var g in gold)
        foreach (var p in predicted)
            pairs.Add((g, p, Score(g, p)));

        var matched = new List<MatchPair>();
        var disputed = new List<MatchPair>();
        var usedGold = new HashSet<string>(StringComparer.Ordinal);
        var usedPred = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in pairs.OrderByDescending(p => p.Score))
        {
            if (pair.Score < DisputedLow) break;
            if (usedGold.Contains(pair.Gold.Id) || usedPred.Contains(pair.Item.Id)) continue;

            if (pair.Score >= MatchThreshold)
            {
                matched.Add(new MatchPair(pair.Gold, pair.Item, pair.Score));
                usedGold.Add(pair.Gold.Id);
                usedPred.Add(pair.Item.Id);
            }

            if (pair.Score is >= DisputedLow and <= DisputedHigh)
                disputed.Add(new MatchPair(pair.Gold, pair.Item, pair.Score));
        }

        return new Result(
            matched,
            gold.Where(g => !usedGold.Contains(g.Id)).ToList(),
            predicted.Where(p => !usedPred.Contains(p.Id)).ToList(),
            disputed);
    }

    public double Score(GoldenItem gold, WorkItem item)
    {
        if (!string.Equals(gold.ProjectId, item.ProjectId, StringComparison.Ordinal)) return 0;

        var titleScore = TextUtil.Cosine(
            CharNGramEmbeddingClient.Embed(gold.Title, embeddingDimensions),
            CharNGramEmbeddingClient.Embed(item.Title, embeddingDimensions));

        var evidenceScore = TextUtil.Jaccard(
            gold.Evidence,
            item.Evidence.Select(e => e.MessageId));

        var score = 0.6 * titleScore + 0.4 * evidenceScore;

        var normalizedTitle = TextUtil.Normalize(item.Title);
        if (gold.Aliases.Any(a => normalizedTitle.Contains(TextUtil.Normalize(a), StringComparison.Ordinal)))
            score += 0.15;

        return Math.Min(1.0, score);
    }
}

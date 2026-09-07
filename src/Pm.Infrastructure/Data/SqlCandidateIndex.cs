using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pm.Application;
using Pm.Application.Text;
using Pm.Domain;

namespace Pm.Infrastructure.Data;

/// <summary>
/// Гибридный поиск кандидатов на слияние: pgvector отбирает похожие по смыслу,
/// полнотекстовый индекс — похожие по словам. Только эмбеддингов мало: на коротких
/// заголовках они путают «фильтр по сроку сдачи» и «срок предоставления контента».
///
/// Финальный скор считается в памяти — сущностей проекта единицы, а формула должна быть
/// той же, что использует прогонщик метрик.
/// </summary>
public sealed class SqlCandidateIndex(PmDbContext db) : ICandidateIndex
{
    public const double VectorWeight = 0.65;
    public const double KeywordWeight = 0.35;

    public async Task<IReadOnlyList<ScoredItem>> FindAsync(
        string projectId, string title, string body, float[] embedding, int limit,
        CancellationToken ct = default)
    {
        var poolSize = Math.Max(limit * 3, 24);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (embedding.Length == db.EmbeddingDimensions)
        {
            var literal = ToVectorLiteral(embedding);
            var byVector = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT w."Id" AS "Value"
                    FROM "WorkItems" w
                    WHERE w."ProjectId" = {0}
                      AND w."Status" <> 'Superseded'
                      AND w."Status" <> 'Cancelled'
                    ORDER BY w."Embedding" <=> CAST({1} AS vector)
                    LIMIT {2}
                    """,
                    projectId, literal, poolSize)
                .ToListAsync(ct);
            foreach (var id in byVector) ids.Add(id);
        }

        var query = title + " " + body;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var byText = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT w."Id" AS "Value"
                    FROM "WorkItems" w
                    WHERE w."ProjectId" = {0}
                      AND w."Status" <> 'Superseded'
                      AND w."Status" <> 'Cancelled'
                      AND ts_match_vq(
                            to_tsvector('russian', coalesce(w."Title", '') || ' ' || coalesce(w."Body", '')),
                            plainto_tsquery('russian', {1}))
                    LIMIT {2}
                    """,
                    projectId, query, poolSize)
                .ToListAsync(ct);
            foreach (var id in byText) ids.Add(id);
        }

        if (ids.Count == 0) return [];

        var idList = ids.ToArray();
        var items = await db.WorkItems.AsNoTracking()
            .Where(w => idList.Contains(w.Id))
            .ToListAsync(ct);

        var candidateKeywords = TextUtil.Keywords(query);

        return items
            .Select(item =>
            {
                var cosine = TextUtil.Cosine(embedding, item.Embedding);
                var keyword = TextUtil.Jaccard(candidateKeywords, TextUtil.Keywords(item.Title + " " + item.Body));
                var score = VectorWeight * cosine + KeywordWeight * keyword;
                return new ScoredItem(item, score, cosine, keyword);
            })
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .ToList();
    }

    /// <summary>Формат литерала pgvector: [0.1,0.2,...]. Инвариантная культура обязательна.</summary>
    public static string ToVectorLiteral(float[] vector)
        => "[" + string.Join(',', vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
}

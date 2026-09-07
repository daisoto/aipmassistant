using Pm.Application;
using Pm.Application.Text;

namespace Pm.Infrastructure.Embeddings;

/// <summary>
/// Офлайн-эмбеддер: хешированные символьные триграммы поверх нормализованного текста.
/// Не заменяет bge-m3, но устойчив к словоформам («фильтра» ≈ «фильтр») и не требует
/// поднятой модели — на нём работают первый запуск, тесты и baseline-прогон метрик.
///
/// Переключение на настоящую модель — секция Embeddings в appsettings; не забыть
/// синхронизировать Dimensions с типом колонки vector(N) и пересоздать базу.
/// </summary>
public sealed class CharNGramEmbeddingClient(int dimensions = 256) : IEmbeddingClient
{
    public int Dimensions { get; } = dimensions;
    public string Name => $"char3gram({Dimensions})";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Embed(text, Dimensions));

    public static float[] Embed(string text, int dimensions)
    {
        var vector = new float[dimensions];
        var normalized = TextUtil.Normalize(text);
        if (normalized.Length == 0) return vector;

        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = $" {word} ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                var gram = padded.AsSpan(i, 3);
                var bucket = (int)(Hash(gram) % (uint)dimensions);
                vector[bucket] += 1f;
            }

            // Стем слова добавляем отдельным сигналом: он вытягивает совпадение
            // разных словоформ одного термина.
            var stem = TextUtil.Stem(word);
            vector[(int)(Hash(stem.AsSpan()) % (uint)dimensions)] += 2f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;

        return vector;
    }

    /// <summary>FNV-1a: стабильный между запусками, в отличие от string.GetHashCode.</summary>
    private static uint Hash(ReadOnlySpan<char> span)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in span)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}

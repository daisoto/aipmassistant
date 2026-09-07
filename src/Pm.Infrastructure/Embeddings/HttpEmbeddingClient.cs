using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Pm.Application;
using Pm.Infrastructure.Llm;

namespace Pm.Infrastructure.Embeddings;

/// <summary>Эмбеддинги через OpenAI-совместимый /v1/embeddings (bge-m3 в Ollama, TEI, infinity).</summary>
public sealed class HttpEmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> options) : IEmbeddingClient
{
    private readonly EmbeddingOptions _opt = options.Value;

    public string Name => $"{_opt.Model}@{_opt.BaseUrl}";
    public int Dimensions => _opt.Dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            "embeddings", new EmbeddingRequest(_opt.Model, text), ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct)
                      ?? throw new InvalidOperationException("Пустой ответ эндпоинта эмбеддингов.");

        var vector = payload.Data.FirstOrDefault()?.Embedding
                     ?? throw new InvalidOperationException("Ответ эмбеддингов не содержит вектора.");

        if (vector.Length != _opt.Dimensions)
        {
            throw new InvalidOperationException(
                $"Модель вернула вектор размерности {vector.Length}, а колонка БД рассчитана на {_opt.Dimensions}. " +
                "Поправьте Embeddings:Dimensions в appsettings и пересоздайте базу.");
        }

        return vector;
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] List<EmbeddingItem> Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}

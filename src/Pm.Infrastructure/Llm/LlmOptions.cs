namespace Pm.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Heuristic — офлайн-baseline на правилах, работает без внешних сервисов.
    /// OpenAiCompatible — любой эндпоинт с /v1/chat/completions: облачный на этапе разработки
    /// или локальные vLLM / Ollama / llama.cpp в закрытом контуре Xpage.
    /// </summary>
    public string Provider { get; set; } = "Heuristic";

    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5:32b-instruct";
    public string? ApiKey { get; set; }
    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Полировать ли формулировки post-meeting моделью (состав пунктов при этом не меняется).</summary>
    public bool PolishPostMeeting { get; set; }

    /// <summary>
    /// Писать в журнал сырой запрос и ответ модели. Включено: проект демонстрационный,
    /// материалы синтетические, а без содержимого журнал не отвечает на главный вопрос —
    /// что именно вернула модель. Перед реальной перепиской выключить.
    /// </summary>
    public bool LogPayloads { get; set; } = true;
}

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>CharNGram — офлайн; Http — эндпоинт /v1/embeddings (bge-m3, e5 и т.п.).</summary>
    public string Provider { get; set; } = "CharNGram";

    /// <summary>
    /// Должно совпадать с типом колонки vector(N). При смене значения базу нужно пересоздать:
    /// Pm.Web делает это по кнопке «Пересоздать базу», Pm.Eval — ключом --recreate.
    /// </summary>
    public int Dimensions { get; set; } = 256;

    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "bge-m3";
    public string? ApiKey { get; set; }
}

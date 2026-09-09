using Pm.Infrastructure.Llm;
using Xunit;

namespace Pm.Tests;

/// <summary>
/// Вырезание JSON из ответа модели. Ломается тихо: разбор падает уже на десериализации,
/// и по логу непонятно, виноват ли промпт или обёртка вокруг ответа.
/// </summary>
public class ExtractJsonTests
{
    [Theory]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("```json\n{\"candidates\":[]}\n```")]
    [InlineData("Вот результат: {\"candidates\":[]}")]
    [InlineData("{\"candidates\":[]} — надеюсь, помог.")]
    public void Object_is_extracted_from_wrapping(string content)
    {
        var json = OpenAiCompatibleLlmClient.ExtractJson(content);

        Assert.StartsWith("{", json);
        Assert.EndsWith("}", json);
        Assert.Contains("candidates", json);
    }

    [Fact]
    public void Nested_objects_are_not_truncated()
    {
        const string content = "```json {\"op\":\"Update\",\"patch\":{\"title\":\"Что-то\"}} ```";

        var json = OpenAiCompatibleLlmClient.ExtractJson(content);

        Assert.Equal("{\"op\":\"Update\",\"patch\":{\"title\":\"Что-то\"}}", json);
    }

    [Fact]
    public void Input_without_braces_is_returned_as_is()
    {
        // Пусть падает дальше на десериализации с осмысленным текстом,
        // а не здесь на индексах.
        Assert.Equal("не могу помочь", OpenAiCompatibleLlmClient.ExtractJson("не могу помочь"));
        Assert.Equal("", OpenAiCompatibleLlmClient.ExtractJson(""));
    }

    [Fact]
    public void Prose_with_a_brace_is_a_known_limitation()
    {
        // ponytail: берётся первая { и последняя } — на прозе с одиночной скобкой
        // это даёт мусор. Лечится строгой схемой у провайдера; полноценный
        // сканер с балансировкой скобок — когда локальная модель начнёт болтать.
        const string content = """Ответ {смотри ниже}: {"candidates":[]}""";

        var json = OpenAiCompatibleLlmClient.ExtractJson(content);

        Assert.Equal("""{смотри ниже}: {"candidates":[]}""", json);
    }
}

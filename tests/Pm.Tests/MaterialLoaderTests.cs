using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pm.Domain;
using Pm.Infrastructure.Materials;
using Xunit;

namespace Pm.Tests;

/// <summary>
/// Загрузчик материалов — теперь ещё и вход извне, то есть граница доверия.
/// Проверяются разбор, разложение стресс-теста по проектам и отказ на кривом файле.
/// </summary>
public class MaterialLoaderTests
{
    private static (MaterialLoader Loader, FakeStore Store) Build()
    {
        var store = new FakeStore();
        return (new MaterialLoader(store, NullLogger<MaterialLoader>.Instance), store);
    }

    private static Stream Json(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private const string Valid = """
        {
          "projectId": "urbankey",
          "projectName": "UrbanKey",
          "projectDescription": "Сайт девелопера",
          "sourceId": "urbankey.client-chat",
          "kind": "ClientChat",
          "title": "Клиентский чат",
          "occurredOn": "2026-09-08",
          "order": 1,
          "messages": [
            { "id": "m1", "at": "09:05", "author": "Клиент", "side": "Client", "text": "Хотим фильтр." },
            { "id": "m2", "at": "09:08", "author": "PM Xpage", "side": "Xpage", "text": "Вернёмся сегодня до 17:00." }
          ]
        }
        """;

    [Fact]
    public async Task Project_source_and_messages_are_read()
    {
        var (loader, store) = Build();

        var loaded = await loader.LoadStreamAsync(Json(Valid), "valid.json");

        Assert.Equal(1, loaded);
        Assert.Equal("UrbanKey", Assert.Single(store.Projects).Name);

        var source = Assert.Single(store.Sources);
        Assert.Equal(SourceKind.ClientChat, source.Kind);
        Assert.Equal(new DateOnly(2026, 9, 8), source.OccurredOn);

        Assert.Equal(2, store.Messages.Count);
        // Порядок задаётся позицией в файле, а не полем at: тредирование опирается на него.
        Assert.Equal([0, 1], store.Messages.Select(m => m.Order));
        Assert.Equal(Side.Xpage, store.Messages[1].AuthorSide);
        Assert.All(store.Messages, m => Assert.Equal("urbankey", m.ProjectId));
    }

    [Fact]
    public async Task Split_by_project_fans_a_mixed_file_into_separate_sources()
    {
        // Стресс-тест: реплики разных проектов в одном файле. Если они осядут одним
        // источником, изоляция контекста между проектами теряется молча.
        const string mixed = """
            {
              "projectId": "stress",
              "sourceId": "stress.mixed",
              "kind": "InternalChat",
              "title": "Смешанный поток",
              "occurredOn": "2026-09-10",
              "splitByProject": true,
              "messages": [
                { "id": "s1", "projectId": "urbankey", "author": "PM", "side": "Xpage", "text": "Первое." },
                { "id": "s2", "projectId": "retailflow", "author": "PM", "side": "Xpage", "text": "Второе." },
                { "id": "s3", "projectId": "urbankey", "author": "PM", "side": "Xpage", "text": "Третье." }
              ]
            }
            """;

        var (loader, store) = Build();

        var loaded = await loader.LoadStreamAsync(Json(mixed), "mixed.json");

        Assert.Equal(2, loaded);
        Assert.Equal(2, store.Sources.Count);
        Assert.Equal(
            ["stress.mixed.retailflow", "stress.mixed.urbankey"],
            store.Sources.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));

        var urbankey = store.Sources.Single(s => s.ProjectId == "urbankey");
        Assert.Equal(2, store.Messages.Count(m => m.SourceId == urbankey.Id));
        Assert.All(store.Messages.Where(m => m.SourceId == urbankey.Id),
            m => Assert.Equal("urbankey", m.ProjectId));
    }

    [Theory]
    [InlineData("""{ "sourceId": "s", "messages": [ { "id": "m" } ] }""", "projectId")]
    [InlineData("""{ "projectId": "p", "messages": [ { "id": "m" } ] }""", "sourceId")]
    [InlineData("""{ "projectId": "p", "sourceId": "s", "messages": [] }""", "сообщений")]
    [InlineData("""{ "projectId": "p", "sourceId": "s", "messages": [ { "text": "без id" } ] }""", "id")]
    public async Task Malformed_file_is_rejected_with_a_readable_message(string content, string expected)
    {
        var (loader, store) = Build();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.LoadStreamAsync(Json(content), "broken.json"));

        Assert.Contains(expected, ex.Message);
        // Отказ не должен оставлять половину файла в хранилище.
        Assert.Empty(store.Sources);
        Assert.Empty(store.Messages);
    }

    [Fact]
    public async Task Non_json_is_rejected_as_data_not_as_a_parser_crash()
    {
        var (loader, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.LoadStreamAsync(Json("это не json"), "junk.json"));

        Assert.Contains("junk.json", ex.Message);
    }

    [Fact]
    public async Task Loading_the_same_file_twice_does_not_duplicate_sources()
    {
        var (loader, store) = Build();

        await loader.LoadStreamAsync(Json(Valid), "valid.json");
        await loader.LoadStreamAsync(Json(Valid), "valid.json");

        Assert.Single(store.Sources);
        Assert.Equal(2, store.Messages.Count);
    }
}

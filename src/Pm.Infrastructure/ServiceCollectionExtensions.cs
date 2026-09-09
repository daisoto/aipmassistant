using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Pgvector.Npgsql;
using Pm.Application;
using Pm.Application.Deadlines;
using Pm.Application.Pipeline;
using Pm.Application.PostMeeting;
using Pm.Application.Views;
using Pm.Infrastructure.Data;
using Pm.Infrastructure.Embeddings;
using Pm.Infrastructure.Llm;
using Pm.Infrastructure.Materials;

namespace Pm.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPmAssistant(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.SectionName));
        services.Configure<EmbeddingOptions>(config.GetSection(EmbeddingOptions.SectionName));

        var embeddingOptions = config.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
                               ?? new EmbeddingOptions();
        var llmOptions = config.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();

        services.AddSingleton(new PmDbContextOptions { EmbeddingDimensions = embeddingOptions.Dimensions });

        var connectionString = config.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5433;Database=aipm;Username=aipm;Password=aipm";

        // DataSource с включённым pgvector: без UseVector() Npgsql не знает типа vector.
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        services.AddDbContext<PmDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector()));

        // Фабрика нужна только журналу LLM: он пишется вне транзакции прогона,
        // иначе запись о провалившемся вызове откатывается вместе с прогоном.
        services.AddDbContextFactory<PmDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector()),
            lifetime: ServiceLifetime.Singleton);

        services.AddScoped<IPmStore, EfPmStore>();
        services.AddScoped<LlmRunContext>();
        services.AddScoped<ICandidateIndex, SqlCandidateIndex>();
        services.AddScoped<MaterialLoader>();

        services.AddSingleton<IDeadlineParser, DeadlineParser>();
        services.AddSingleton<QuoteValidator>();
        services.AddSingleton<Threader>();
        services.AddSingleton<PhrasingGuard>();
        services.AddSingleton<PostMeetingRenderer>();
        services.AddSingleton<ResolverOptions>();
        services.AddScoped<Resolver>();
        services.AddScoped<PipelineRunner>();
        services.AddScoped<PostMeetingComposer>();
        services.AddScoped<Exporter>();

        AddLlm(services, llmOptions);
        AddEmbeddings(services, embeddingOptions);

        return services;
    }

    private static void AddLlm(IServiceCollection services, LlmOptions options)
    {
        if (string.Equals(options.Provider, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>(client =>
            {
                client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            });
        }
        else
        {
            // Scoped, а не singleton: baseline теперь тоже пишет журнал, а IPmStore — scoped.
            services.AddScoped<ILlmClient, HeuristicLlmClient>();
        }
    }

    private static void AddEmbeddings(IServiceCollection services, EmbeddingOptions options)
    {
        if (string.Equals(options.Provider, "Http", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IEmbeddingClient, HttpEmbeddingClient>(client =>
            {
                client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
                client.Timeout = TimeSpan.FromSeconds(60);
                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            });
        }
        else
        {
            services.AddSingleton<IEmbeddingClient>(_ => new CharNGramEmbeddingClient(options.Dimensions));
        }
    }

    private static string EnsureTrailingSlash(string url)
        => url.EndsWith('/') ? url : url + "/";
}

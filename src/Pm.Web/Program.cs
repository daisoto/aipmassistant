using Microsoft.Extensions.Options;
using Pm.Infrastructure;
using Pm.Infrastructure.Data;
using Pm.Infrastructure.Llm;
using Pm.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddPmAssistant(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Схема создаётся на старте: на четырёхдневном MVP модель меняется по нескольку раз в день,
// и держать миграции в актуальном состоянии дороже, чем пересоздать базу.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var db = scope.ServiceProvider.GetRequiredService<PmDbContext>();
    var llm = scope.ServiceProvider.GetRequiredService<IOptions<LlmOptions>>().Value;

    try
    {
        await DbInitializer.InitializeAsync(db, logger);
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Не удалось подготовить базу. Проверьте, что поднят PostgreSQL с расширением pgvector: " +
            "docker compose up -d");
        throw;
    }

    logger.LogInformation("Провайдер LLM: {Provider}", llm.Provider);
    if (string.Equals(llm.Provider, "Heuristic", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogInformation(
            "Работает офлайн-baseline на правилах. Для подключения модели укажите " +
            "Llm:Provider=OpenAiCompatible и Llm:BaseUrl в appsettings.json.");
    }
}

app.Run();

namespace Reon.Api.Extensions;

/// <summary>
/// Хостинг, конвейер middleware и карта маршрутов приложения.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Ограничивает размер тела запроса и multipart-загрузки значением
    /// <c>FileStorage:MaxFileSizeBytes</c>.
    /// </summary>
    /// <param name="builder">Построитель веб-приложения.</param>
    /// <returns>Тот же построитель для цепочки вызовов.</returns>
    public static WebApplicationBuilder AddReonApiUploadLimits(this WebApplicationBuilder builder)
    {
        var maxFileSize = builder.Configuration.GetValue("FileStorage:MaxFileSizeBytes", 1_073_741_824L);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = maxFileSize;
        });
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxFileSize;
        });

        return builder;
    }

    /// <summary>
    /// Применяет миграции EF Core при <c>Database:AutoMigrate = true</c>.
    /// </summary>
    /// <param name="app">Приложение.</param>
    /// <returns>Задача применения миграций.</returns>
    public static async Task ApplyReonApiMigrationsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:AutoMigrate", false))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ReonDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    /// <summary>
    /// Собирает конвейер middleware: журналирование запросов, обработка ошибок,
    /// CORS, WebSocket и единый формат статус-страниц.
    /// </summary>
    /// <param name="app">Приложение.</param>
    /// <returns>То же приложение для цепочки вызовов.</returns>
    public static WebApplication UseReonApi(this WebApplication app)
    {
        app.UseReonApiRequestLogging();
        app.UseExceptionHandler();

        if (app.Environment.IsDevelopment())
        {
            app.UseCors();
        }

        app.UseWebSockets();
        app.UseStatusCodePages(async context =>
        {
            var response = context.HttpContext.Response;
            if (response.HasStarted)
            {
                return;
            }

            response.ContentType = "application/problem+json";
            await response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = response.StatusCode,
                    Title = ReasonPhrases.GetReasonPhrase(response.StatusCode),
                    Instance = context.HttpContext.Request.Path,
                });
        });

        return app;
    }

    /// <summary>
    /// Регистрирует контроллеры, real-time контур администратора,
    /// документацию и health checks.
    /// </summary>
    /// <param name="app">Приложение.</param>
    /// <returns>То же приложение для цепочки вызовов.</returns>
    public static WebApplication MapReonApiEndpoints(this WebApplication app)
    {
        app.MapControllers();
        app.MapAdminEvents();
        app.MapReonApiDocumentation();

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        });

        return app;
    }
}

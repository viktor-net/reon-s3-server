namespace Reon.Api.Extensions;

/// <summary>
/// Регистрация сервисов приложения. Program.cs остаётся композиционным корнем:
/// здесь собраны только регистрации, без построения конвейера и маршрутов.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует сервисы API: MVC, CORS, ProblemDetails, OpenAPI,
    /// инфраструктуру, файловое хранилище, health checks и real-time контур.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <param name="configuration">Конфигурация приложения.</param>
    /// <param name="environment">Окружение хостинга.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    public static IServiceCollection AddReonApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddControllers();
        services.AddReonApiCors(environment);
        services.AddReonApiProblemDetails();
        services.AddReonApiOpenApi();
        services.AddInfrastructure(configuration);
        services.AddReonApiFileStorage(configuration);
        services.AddReonApiHealthChecks();
        services.AddExceptionHandler<CoreExceptionHandler>();
        services.AddReonApiEvents();

        return services;
    }

    private static IServiceCollection AddReonApiCors(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy
                        .SetIsOriginAllowed(origin =>
                            origin == "null" ||
                            (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback))
                        .AllowAnyHeader()
                        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "QUERY"));
            });
        }

        return services;
    }

    private static IServiceCollection AddReonApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            };
        });

        return services;
    }

    private static IServiceCollection AddReonApiFileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<FileStorageOptions>(
            configuration.GetSection(FileStorageOptions.SectionName));
        services.AddScoped<FileStorageService>();

        return services;
    }

    private static IServiceCollection AddReonApiHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<S3HealthCheck>("s3", tags: ["ready"]);

        return services;
    }

    private static IServiceCollection AddReonApiEvents(this IServiceCollection services)
    {
        // Административный real-time контур: WebSocket-хаб и цикл heartbeat.
        services.AddSingleton<AdminEventHub>();
        services.AddSingleton<IStorageEventPublisher>(provider =>
            provider.GetRequiredService<AdminEventHub>());
        services.AddHostedService<AdminEventsHeartbeatService>();

        return services;
    }
}

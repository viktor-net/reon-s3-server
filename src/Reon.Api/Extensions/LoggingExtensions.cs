using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Reon.Api.Extensions;

/// <summary>
/// Настройка Serilog: консоль из конфигурации и суточный NDJSON-файл в каталоге logs.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Подключает Serilog: консоль из конфигурации и суточный NDJSON-файл
    /// в каталоге logs рядом с решением.
    /// </summary>
    /// <param name="builder">Построитель веб-приложения.</param>
    /// <returns>Тот же построитель для цепочки вызовов.</returns>
    public static WebApplicationBuilder AddReonApiLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        var logDirectory = ResolveLogDirectory(builder.Environment.ContentRootPath);

        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(logDirectory, "reon-.ndjson"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 52_428_800,
                rollOnFileSizeLimit: true,
                shared: true));

        return builder;
    }

    /// <summary>
    /// Включает журналирование HTTP-запросов с уровнем, зависящим от статуса ответа.
    /// </summary>
    /// <param name="app">Приложение.</param>
    /// <returns>То же приложение для цепочки вызовов.</returns>
    public static WebApplication UseReonApiRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (httpContext, _, exception) =>
                exception is not null
                    ? LogEventLevel.Error
                    : httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError &&
                      httpContext.Response.StatusCode != StatusCodes.Status501NotImplemented
                        ? LogEventLevel.Error
                        : httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
                            ? LogEventLevel.Warning
                            : LogEventLevel.Information;
        });

        return app;
    }

    private static string ResolveLogDirectory(string contentRoot)
    {
        var directory = new DirectoryInfo(contentRoot);

        while (directory is not null && !directory.EnumerateFiles("*.slnx").Any())
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? contentRoot;
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        return logs;
    }
}

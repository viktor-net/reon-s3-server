using System.Globalization;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.SystemConsole.Themes;

namespace Reon.Api.Extensions;

/// <summary>
/// Настройка Serilog: человекочитаемая консоль в Development
/// и JSON в stdout в остальных окружениях. Файлы не используются.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>Ключ конфигурации, принудительно включающий или отключающий JSON в консоли.</summary>
    private const string JsonConsoleSetting = "Serilog:Console:Json";

    /// <summary>Шаблон человекочитаемой строки для локальной разработки.</summary>
    private const string DevelopmentOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Подключает Serilog и пишет только в консоль: в Development — текст,
    /// в остальных окружениях — компактный JSON (по строке на событие) в stdout,
    /// чтобы его мог читать OpenTelemetry Collector.
    /// </summary>
    /// <param name="builder">Построитель веб-приложения.</param>
    /// <returns>Тот же построитель для цепочки вызовов.</returns>
    /// <remarks>
    /// Формат выбирается по <c>ASPNETCORE_ENVIRONMENT</c>, а не по конфигурации
    /// сборки, чтобы поведение совпадало с окружением контейнера. Значение
    /// <c>Serilog:Console:Json</c> (или переменная <c>Serilog__Console__Json</c>)
    /// переопределяет выбор явно.
    /// </remarks>
    public static WebApplicationBuilder AddReonApiLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        var useJsonConsole = builder.Configuration.GetValue<bool?>(JsonConsoleSetting)
            ?? !builder.Environment.IsDevelopment();

        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                // Окружение нужно коллектору, чтобы проставить
                // deployment.environment.name в логи из stdout.
                .Enrich.WithProperty(
                    "deployment.environment.name",
                    builder.Environment.EnvironmentName.ToLowerInvariant());

            if (useJsonConsole)
            {
                configuration.WriteTo.Console(new CompactJsonFormatter());
            }
            else
            {
                configuration.WriteTo.Console(
                    outputTemplate: DevelopmentOutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture,
                    theme: AnsiConsoleTheme.Code);
            }
        });

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
}

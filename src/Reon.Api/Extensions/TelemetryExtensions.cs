using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.AWS;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Reon.Api.Telemetry;

namespace Reon.Api.Extensions;

/// <summary>
/// Настройка OpenTelemetry: спаны ASP.NET Core, исходящих HTTP-запросов,
/// AWS SDK (S3) и Npgsql (Postgres) отправляются в OTLP-коллектор.
/// </summary>
public static class TelemetryExtensions
{
    /// <summary>Имя сервиса в телеметрии.</summary>
    public const string ServiceName = "reon-s3-api";

    /// <summary>Профиль наблюдаемости REON.</summary>
    public const string ObservabilityProfile = "REON-OBS-1.0";

    private const string OtlpEndpointSetting = "Observability:OtlpEndpoint";
    private const string DefaultOtlpEndpoint = "http://localhost:4317";

    /// <summary>
    /// Регистрирует OpenTelemetry SDK и экспортёр OTLP/gRPC.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <param name="configuration">Конфигурация приложения.</param>
    /// <param name="environment">Окружение хостинга.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    /// <remarks>
    /// Адрес коллектора берётся из <c>Observability:OtlpEndpoint</c> или
    /// стандартной переменной <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>; по умолчанию
    /// используется <c>http://localhost:4317</c> для локального запуска.
    /// </remarks>
    public static IServiceCollection AddReonTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var endpoint = configuration[OtlpEndpointSetting]
            ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? DefaultOtlpEndpoint;

        // Идентификатор экземпляра фиксируется на время жизни процесса.
        var instanceId = Guid.NewGuid().ToString("N");
        var version = typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: ServiceName,
                    serviceNamespace: "reon",
                    serviceVersion: version,
                    serviceInstanceId: instanceId)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = environment.EnvironmentName.ToLowerInvariant(),
                    ["reon.observability.profile"] = ObservabilityProfile,
                }))
            .WithTracing(tracing => tracing
                // Npgsql сам создаёт ActivitySource "Npgsql" для запросов к Postgres.
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(options => options.RecordException = true)
                .AddHttpClientInstrumentation()
                .AddAWSInstrumentation(options =>
                    options.SemanticConventionVersion = SemanticConventionVersion.V1_40_0)
                // Убирает ключ объекта из спанов S3 и добавляет s3.bucket.
                .AddProcessor(new S3SpanProcessor())
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(endpoint);
                    options.Protocol = OtlpExportProtocol.Grpc;
                }));

        return services;
    }
}

using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddControllers();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
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

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddOpenApi("api", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Reon S3 Server";
        document.Info.Description =
            "Сервис хранения файлов и учёта их описаний.\n\n" +
            "Базовый API (разделы Files, Uploads и Storage) работает с " +
            "файловым доменом в одном бакете; административный фасад (раздел Admin) " +
            "охватывает все бакеты S3: сырые объекты, версии, квоты и журнал операций.";
        return Task.CompletedTask;
    });
    options.AddDocumentTransformer<OpenApiTagDescriptionsTransformer>();
    options.AddDocumentTransformer<OpenApiQueryOperationTransformer>();
    options.AddOperationTransformer<OpenApiExamplesOperationTransformer>();
    options.AddOperationTransformer<OpenApiTextTransformer>();
});
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.Configure<FileStorageOptions>(
    builder.Configuration.GetSection(FileStorageOptions.SectionName));
builder.Services.AddScoped<FileStorageService>();

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<S3HealthCheck>("s3", tags: ["ready"]);

builder.Services.AddExceptionHandler<CoreExceptionHandler>();

// Административный real-time контур: WebSocket-хаб и цикл heartbeat.
builder.Services.AddSingleton<AdminEventHub>();
builder.Services.AddSingleton<IStorageEventPublisher>(provider => provider.GetRequiredService<AdminEventHub>());
builder.Services.AddHostedService<AdminEventsHeartbeatService>();

var maxFileSize = builder.Configuration.GetValue("FileStorage:MaxFileSizeBytes", 1_073_741_824L);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxFileSize;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxFileSize;
});

var app = builder.Build();

if (app.Configuration.GetValue("Database:AutoMigrate", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ReonDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseSerilogRequestLogging();

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

app.MapControllers();
app.MapAdminEvents();
app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options
        .WithTitle("Reon S3 Server")
        // Русская локаль переводит подписи Scalar и встроенные описания схем.
        .WithLocalization(new ScalarLocalizationOptions { Locale = "ru" })
        // Убираем продвижение Scalar: CTA "Generate SDKs & MCP",
        // кнопку клиента и чат Agent Scalar ("Ask AI").
        .DisableMcp()
        .HideClientButton()
        .DisableAgent()
        // Скрывает всю панель developer tools: Developer Tools, Configure,
        // Share, Deploy, Generate SDK — это подписи пунктов меню Scalar.
        .HideDeveloperTools()
        // Порядок вкладок Client Libraries: shell, затем языки проекта.
        // Остальные клиенты остаются под кнопкой More.
        .WithFeaturedClients(
            new(ScalarTarget.Shell, ScalarClient.Curl),
            new(ScalarTarget.JavaScript, ScalarClient.Axios),
            new(ScalarTarget.CSharp, ScalarClient.HttpClient),
            new(ScalarTarget.Java, ScalarClient.OkHttp))
        // Штатной опции для подписи "Powered by Scalar" нет — гасим её CSS.
        // Заодно снимаем обрезку описаний групп и операций: по умолчанию Scalar
        // показывает только 7 строк и не даёт их раскрыть.
        .WithCustomCss(
            "a[href*=\"utm_source=powered-by\"]{display:none!important}" +
            ".scalar-app .section-content .line-clamp-\\(--markdown-clamp\\)" +
            "{-webkit-line-clamp:unset;display:block;overflow:visible}")
        // Обёртка Scalar для .NET 2.18.3 не отдаёт defaultOpenFirstTag,
        // поэтому настройка приходит из отдельного JS-модуля. Путь
        // относительный, чтобы работало и при размещении под PathBase.
        .WithJavaScriptConfiguration("scalar/admin-config.js");
});

// Не раскрывать первый тег при открытии: в сайдбаре всё должно быть свёрнуто.
app.MapGet("/scalar/admin-config.js", () =>
        Results.Text("export default { defaultOpenFirstTag: false };", "text/javascript"))
    .ExcludeFromDescription();

// Scalar ищет OpenAPI относительно URL интерфейса. Оставляем псевдонимы
// для /scalar/api и /scalar/api/, чтобы UI мог загрузить /openapi/api.json.
app.MapGet("/scalar/openapi/{documentName}.json", (string documentName) =>
        Results.Redirect($"/openapi/{documentName}.json", permanent: false))
    .ExcludeFromDescription();
app.MapGet("/scalar/{documentName}/openapi/{openApiDocument}.json", (string documentName, string openApiDocument) =>
        Results.Redirect($"/openapi/{openApiDocument}.json", permanent: false))
    .ExcludeFromDescription();

app.MapGet("/", () => Results.Redirect("/scalar/api"))
    .ExcludeFromDescription();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.Run();

public partial class Program
{
}

namespace Reon.Api.Extensions;

/// <summary>
/// OpenAPI-документ и интерактивная документация Scalar, включая псевдонимы
/// маршрутов, которые нужны UI для загрузки спецификации.
/// </summary>
public static class OpenApiExtensions
{
    /// <summary>
    /// Регистрирует генерацию OpenAPI-документа и трансформеры описаний.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    public static IServiceCollection AddReonApiOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("api", options =>
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
            options.AddOperationTransformer<OpenApiParameterExamplesTransformer>();
            options.AddOperationTransformer<OpenApiTextTransformer>();
        });

        return services;
    }

    /// <summary>
    /// Подключает OpenAPI-документ, интерфейс Scalar и вспомогательные маршруты,
    /// по которым UI загружает спецификацию.
    /// </summary>
    /// <param name="app">Приложение.</param>
    /// <returns>То же приложение для цепочки вызовов.</returns>
    public static WebApplication MapReonApiDocumentation(this WebApplication app)
    {
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

        return app;
    }
}

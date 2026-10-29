using Microsoft.AspNetCore.Mvc.Routing;

namespace Reon.Api.Routing;

/// <summary>
/// Маршрутизирует действие на HTTP-метод QUERY (RFC 10008): безопасный
/// запрос с телом, который не меняет состояние ресурса. Нужен для выборок,
/// которые не помещаются в query-строку.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class HttpQueryAttribute : HttpMethodAttribute
{
    private static readonly string[] Methods = [HttpMethod.Query.Method];

    public HttpQueryAttribute()
        : base(Methods)
    {
    }

    public HttpQueryAttribute(string template)
        : base(Methods, template)
    {
    }
}

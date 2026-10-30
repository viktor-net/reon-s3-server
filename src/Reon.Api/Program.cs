using Reon.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder
    .AddReonApiLogging()
    .AddReonApiUploadLimits();

builder.Services.AddReonApi(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.ApplyReonApiMigrationsAsync();

app
    .UseReonApi()
    .MapReonApiEndpoints();

app.Run();

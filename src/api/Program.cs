using api;
using features;
using infra.persistence.postgre;
using service.defaults;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddSparkrockRwc()
    .WithPostgre()
    .WithFeatures()
    .WithApi();

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS for external API clients (Scalar's hosted client fetches the OpenAPI document cross-origin)
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddPolicy("Development", policy =>
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()));
}

WebApplication app = builder.Build();

app.UseApiErrorHandling();

if (app.Environment.IsDevelopment())
{
    app.UseCors("Development");
    app.UseSwagger();
    app.MapScalarApiReference(options =>
    {
        options
            .AddDocument("v1")
            .WithOpenApiRoutePattern("/swagger/{documentName}/swagger.json")
            .WithDynamicBaseServerUrl()
            .WithTitle("Sparkrock RWC API")
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
            .AddPreferredSecuritySchemes(["Bearer"])
            .AddHttpAuthentication("Bearer", scheme => { scheme.Token = ""; });
    });
    app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromApiReference().ExcludeFromDescription();
}

app.UseSparkrockRwc();
app.Run();
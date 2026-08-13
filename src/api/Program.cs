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

WebApplication app = builder.Build();

// Transport before anything reads the request. HSTS only outside Development: the header is cached
// by the browser per host, and localhost is a host, so issuing it in Development pins every other
// http://localhost project the developer runs to HTTPS for the max-age.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseApiErrorHandling();

// One policy, an explicit origin list, no credentials — see ServiceExtensions.AddCors. The list is
// empty unless configured, so this is inert by default rather than off by default.
app.UseCors(api.ServiceExtensions.CorsPolicyName);

if (app.Environment.IsDevelopment())
{
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

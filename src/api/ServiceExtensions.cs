using api.Errors;
using domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using service.defaults;

namespace api;

public static class ServiceExtensions
{
    /// <summary>
    ///     Registers the HTTP edge: the acting identity, the error envelope and the exception handlers.
    /// </summary>
    /// <remarks>
    ///     Identity is registered here rather than in the persistence layer so that the integration
    ///     test host and the importer can supply their own without inheriting an anonymous one.
    /// </remarks>
    public static ISparkrockRwcBuilder WithApi(this ISparkrockRwcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Fails closed before anything anonymous is registered.
        DeploymentGuard.EnsureStubIdentityIsPermitted(builder.Environment, builder.Configuration);

        IServiceCollection services = builder.Services;

        services.AddScoped<ICurrentUser, StubCurrentUser>();
        services.AddScoped<IAuditOverride, AuditOverride>();

        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Customize);

        // Registration order is dispatch order: most specific first.
        services.AddExceptionHandler<ValidationExceptionHandler>();
        services.AddExceptionHandler<DomainExceptionHandler>();

        return builder;
    }

    /// <summary>
    ///     Adds the pieces of the pipeline that must sit outside <c>UseExceptionHandler</c>.
    /// </summary>
    /// <remarks>
    ///     <c>UseStatusCodePages</c> is not optional. The ProblemDetails customisation covers
    ///     responses that reach an exception handler; routing 404s, 405s, 415s and minimal-API
    ///     binding failures never do, so without this a client sees two different error shapes from
    ///     the same API.
    /// </remarks>
    public static WebApplication UseApiErrorHandling(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        return app;
    }
}

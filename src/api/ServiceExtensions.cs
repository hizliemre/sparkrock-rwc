using api.Errors;
using domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using service.defaults;

namespace api;

public static class ServiceExtensions
{
    /// <summary>The single CORS policy. There is no second one, and no per-environment variant.</summary>
    public const string CorsPolicyName = "SparkrockRwc";

    /// <summary>
    ///     Configuration key holding the origins permitted to call the API cross-origin.
    /// </summary>
    /// <remarks>
    ///     A string array, absent from every committed file. Absent means no cross-origin caller is
    ///     permitted, which is the correct default for an API whose only UI is served same-origin.
    /// </remarks>
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";

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

        // Not the real one. Begin() forges audit attribution and suppresses CreatedAt stamping, and
        // it is public on a public interface — see NullAuditOverride.
        services.AddScoped<IAuditOverride, NullAuditOverride>();

        AddCors(builder);

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

    /// <summary>
    ///     One policy, an explicit origin list, and no credentials.
    /// </summary>
    /// <remarks>
    ///     The previous policy reflected any origin (<c>SetIsOriginAllowed(_ => true)</c>) and paired
    ///     it with <c>AllowCredentials</c> — the one combination that is always unsafe, because the
    ///     browser then treats every page the developer happens to visit as entitled to make
    ///     credentialed calls and read the responses. With no authentication yet, the same-origin
    ///     policy is the <em>only</em> thing keeping a random page away from every school's roster, and
    ///     that policy was the thing being switched off. Scalar is served same-origin at
    ///     <c>/scalar/v1</c> and never needed any of it.
    ///     <para>
    ///         Registered in every environment with an empty list by default: a policy that allows
    ///         nothing is inert, whereas a Development-only policy invites the wildcard back the first
    ///         time someone needs a cross-origin caller in a shared environment. Credentials are not
    ///         configurable — an allowlist plus credentials is a decision to be taken alongside real
    ///         authentication, not inherited from a scaffold.
    ///     </para>
    ///     <para>
    ///         The banned-symbol entries for <c>AllowAnyOrigin</c> and <c>SetIsOriginAllowed</c> stop a
    ///         wildcard being written in code. They do <em>not</em> stop one arriving through
    ///         configuration: <c>WithOrigins("*")</c> sets <c>AllowAnyOrigin</c> on the resulting
    ///         policy, so a single <c>"*"</c> in <c>Cors:AllowedOrigins</c> — from any provider,
    ///         including an environment variable on a deployment nobody reviewed — reproduces exactly
    ///         the policy that was removed. <see cref="ValidateOrigins" /> is the half the analyzer
    ///         cannot cover.
    ///     </para>
    /// </remarks>
    private static void AddCors(ISparkrockRwcBuilder builder)
    {
        string[] origins = builder.Configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? [];

        ValidateOrigins(origins);

        builder.Services.AddCors(options => options.AddPolicy(
            CorsPolicyName,
            policy => policy
                .WithOrigins(origins)
                .AllowAnyMethod()
                .AllowAnyHeader()));
    }

    /// <summary>
    ///     Rejects any configured origin that is not a concrete scheme-and-authority.
    /// </summary>
    /// <remarks>
    ///     Throws at startup rather than filtering the bad entry out. A silently dropped origin is a
    ///     CORS failure the operator debugs in the browser console; a refused start names the problem
    ///     at the point the mistake was made. This mirrors the deployment guard, which also refuses to
    ///     start rather than degrading.
    /// </remarks>
    internal static void ValidateOrigins(IReadOnlyList<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        for (int index = 0; index < origins.Count; index++)
        {
            string origin = origins[index];

            if (origin == "*")
                throw new InvalidOperationException(
                    $"'{AllowedOriginsKey}[{index}]' is \"*\". WithOrigins(\"*\") sets AllowAnyOrigin on "
                    + "the policy, which is the wildcard this configuration exists to prevent. List "
                    + "each origin explicitly.");

            if (string.IsNullOrWhiteSpace(origin)
                || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException(
                    $"'{AllowedOriginsKey}[{index}]' is '{origin}', which is not an absolute http or "
                    + "https URI. An origin the browser cannot match is silently never allowed.");

            // An origin is scheme, host and port and nothing else. CORS compares it against the
            // browser's Origin header ordinally, and that header never carries a path — not even a
            // bare trailing slash. So "https://school.example/" is not a stricter spelling of the
            // same origin, it is one that can never match, and the symptom is an unexplained CORS
            // rejection rather than a startup error. Compared against the authority form rather than
            // by inspecting AbsolutePath, which reports "/" for both spellings.
            if (!string.Equals(origin, parsed.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"'{AllowedOriginsKey}[{index}]' is '{origin}'. An origin carries scheme, host and "
                    + $"port only — write it as '{parsed.GetLeftPart(UriPartial.Authority)}'. A path, "
                    + "query, fragment or trailing slash never matches the browser's Origin header.");
        }
    }
}

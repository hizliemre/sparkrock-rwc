using System.Reflection;
using Carter;
using features.Behaviors;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using service.defaults;

namespace features;

public static class ServiceExtensions
{
    public static ISparkrockRwcBuilder WithFeatures(this ISparkrockRwcBuilder builder)
    {
        IServiceCollection services = builder.Services;

        services.AddEndpointsApiExplorer();

        // DEC-12 requires submitted dates to be bounded and calls the lower bound "a configured
        // back-dating window". Bound rather than constant because the number (30) is an engineering
        // default with no business input, and V-25 ● is unsigned — the section may be absent, in which
        // case the defaults on the options type apply.
        services.Configure<Attendance.AttendanceSaveOptions>(
            builder.Configuration.GetSection(Attendance.AttendanceSaveOptions.SectionName));

        Assembly featuresAssembly = typeof(ServiceExtensions).Assembly;

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(featuresAssembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        // includeInternalTypes: validators are declared internal inside their feature class
        services.AddValidatorsFromAssembly(featuresAssembly, includeInternalTypes: true);

        Assembly? entryAssembly = Assembly.GetEntryAssembly();

        DependencyContextAssemblyCatalog assemblyCatalog = new(entryAssembly);
        services.AddCarter(assemblyCatalog);

        return builder;
    }

    /// <summary>
    ///     Mounts every Carter module under the versioned API group.
    /// </summary>
    /// <remarks>
    ///     Modules map paths relative to this group. Writing <c>/api/...</c> inside a module doubles
    ///     the prefix, which is why the group owns the version segment rather than each route.
    /// </remarks>
    public static void UseSparkrockRwc(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("api/v1").MapCarter();
    }
}
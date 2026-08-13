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

    public static void UseSparkrockRwc(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("api").MapCarter();
    }
}
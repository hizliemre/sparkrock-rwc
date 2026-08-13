using infra.persistence.postgre.Interceptors;
using infra.persistence.sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using service.defaults;

namespace infra.persistence.postgre;

public static class ServiceExtensions
{
    public static ISparkrockRwcBuilder WithPostgre(this ISparkrockRwcBuilder builder)
    {
        IServiceCollection services = builder.Services;
        IConfiguration configuration = builder.Configuration;
        
        string connectionString = configuration.GetConnectionString("sparkrock-rwc") ?? throw new ArgumentException("Connection string sparkrock-rwc not found.");
        
        NpgsqlDataSourceBuilder dataSourceBuilder = new(connectionString);
        NpgsqlDataSource dataSource = dataSourceBuilder.Build();

        // TimeProvider is not auto-registered; without this the first save fails at DI resolution.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, not singleton: it consumes a scoped ICurrentUser, and a singleton would capture
        // the first request's identity for the lifetime of the process.
        services.AddScoped<AuditableEntityInterceptor>();

        services.AddDbContext<SparkrockRwcDbContext>(ConfigureDatasourceOptions);
        services.AddScoped<IDbContext>(provider => provider.GetRequiredService<SparkrockRwcDbContext>());
        services.AddDbContextFactory<SparkrockRwcDbContext>(ConfigureDatasourceOptions, ServiceLifetime.Scoped);

        return builder;

        void ConfigureDatasourceOptions(IServiceProvider provider, DbContextOptionsBuilder options)
        {
            options.UseNpgsql(dataSource, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
            // Must match DbContextFactory (design-time), otherwise migrations create snake_case tables
            // while the running app queries PascalCase ones.
            options.UseSnakeCaseNamingConvention();
            options.AddInterceptors(provider.GetRequiredService<AuditableEntityInterceptor>());
        }
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace infra.persistence.postgre;

internal sealed class DbContextFactory: IDesignTimeDbContextFactory<SparkrockRwcDbContext>
{
    public SparkrockRwcDbContext CreateDbContext(string[] args)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddJsonFile("appsettings.json")
            .Build();

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql(configuration.GetConnectionString("SparkrockRwc")).UseSnakeCaseNamingConvention()
            .Options;

        return new SparkrockRwcDbContext(options);
    }
}
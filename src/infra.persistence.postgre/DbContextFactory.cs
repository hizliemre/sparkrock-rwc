using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace infra.persistence.postgre;

/// <summary>
///     Design-time factory for <c>dotnet ef</c>. Never used at runtime.
/// </summary>
/// <remarks>
///     Reads the connection string from user secrets or the environment only. It previously read a
///     tracked <c>appsettings.json</c> that carried a password and was copied into every consumer's
///     build output — and DEC-13's licence position makes anything in this repository publishable by
///     obligation, so a credential in a tracked file is a disclosure rather than untidiness.
///     <para>
///         Set <c>ConnectionStrings__SparkrockRwc</c>, or
///         <c>dotnet user-secrets set "ConnectionStrings:SparkrockRwc" "&lt;value&gt;" --project src/infra.persistence.postgre</c>.
///     </para>
/// </remarks>
internal sealed class DbContextFactory : IDesignTimeDbContextFactory<SparkrockRwcDbContext>
{
    private const string ConnectionStringName = "SparkrockRwc";

    public SparkrockRwcDbContext CreateDbContext(string[] args)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets<DbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        string? connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' was not found. Set the environment variable "
                + $"ConnectionStrings__{ConnectionStringName}, or run: dotnet user-secrets set "
                + $"\"ConnectionStrings:{ConnectionStringName}\" \"<value>\" --project src/infra.persistence.postgre");
        }

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new SparkrockRwcDbContext(options);
    }
}

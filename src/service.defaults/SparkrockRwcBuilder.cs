using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace service.defaults;

public interface ISparkrockRwcBuilder
{
    /// <summary>
    ///     The service collection being configured.
    /// </summary>
    IServiceCollection Services { get; }
    /// <summary>
    ///     The application configuration.
    /// </summary>
    IConfiguration Configuration { get; }
    /// <summary>
    ///     The host environment.
    /// </summary>
    IHostEnvironment Environment { get; }
    /// <summary>
    ///     The underlying host application builder.
    /// </summary>
    IHostApplicationBuilder HostBuilder { get; }
}

public sealed class SparkrockRwcBuilder : ISparkrockRwcBuilder
{
    internal SparkrockRwcBuilder(IHostApplicationBuilder builder)
    {
        HostBuilder = builder;
        Services = builder.Services;
        Configuration = builder.Configuration;
        Environment = builder.Environment;
    }

    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }
    public IHostEnvironment Environment { get; }
    public IHostApplicationBuilder HostBuilder { get; }
}
using Microsoft.Extensions.Hosting;

namespace service.defaults;

public static class SparkrockRwcBuilderExtensions
{
    public static ISparkrockRwcBuilder AddSparkrockRwc(this IHostApplicationBuilder builder)
    {
        return new SparkrockRwcBuilder(builder);
    }
}
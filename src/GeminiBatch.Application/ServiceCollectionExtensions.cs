using Microsoft.Extensions.DependencyInjection;

namespace GeminiBatch.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<BatchProcessor>();
        return services;
    }
}

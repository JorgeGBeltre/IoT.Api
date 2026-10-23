using System.Reflection;
using IoT.Protocols;
using Microsoft.Extensions.DependencyInjection;

namespace IoT.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
        });

        services.AddSingleton<ICompositeProtocolAdapter>(_ => CompositeProtocolAdapter.CreateDefault());

        return services;
    }
}

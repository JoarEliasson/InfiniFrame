using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LocalControlsUi.Services;

public static class LocalControlsServices
{
    /// <summary>Registers app-owned fake services; does not register a renderer or web server.</summary>
    public static IServiceCollection AddLocalControlsUi(this IServiceCollection services)
    {
        services.TryAddSingleton<FakeControlBackend>();
        services.TryAddSingleton<FakeLoginService>();
        return services;
    }
}

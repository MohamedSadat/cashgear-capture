using CashGear.Capture.Engine.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Capture.Engine;

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<StartupDiagnostics>();
        return services;
    }
}

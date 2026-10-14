using System.Buffers;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CashGear.Capture.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureCore(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ArrayPool<byte>.Shared);
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        return services;
    }
}

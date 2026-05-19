// Copyright (c) Inverted Software. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus;

public static class AzureServiceBusServiceCollectionExtensions
{
    public static IServiceCollection AddAzureServiceBusQueueProvider(
        this IServiceCollection services,
        Action<AzureServiceBusOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.RemoveAll<IQueueProvider>();
        services.AddSingleton<IQueueProvider>(sp =>
        {
            var opts = new AzureServiceBusOptions();
            configure(opts);
            var logger = sp.GetService(typeof(Microsoft.Extensions.Logging.ILogger<AzureServiceBusQueueProvider>))
                as Microsoft.Extensions.Logging.ILogger<AzureServiceBusQueueProvider>;
            return new AzureServiceBusQueueProvider(opts, logger);
        });
        return services;
    }
}

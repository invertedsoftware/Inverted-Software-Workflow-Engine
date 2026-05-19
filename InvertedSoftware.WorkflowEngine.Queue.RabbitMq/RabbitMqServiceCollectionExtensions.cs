// Copyright (c) Inverted Software. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvertedSoftware.WorkflowEngine.Queue.RabbitMq;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="RabbitMqQueueProvider"/> as the singleton
    /// <see cref="IQueueProvider"/>. Replaces any prior provider registration.
    /// </summary>
    public static IServiceCollection AddRabbitMqQueueProvider(
        this IServiceCollection services,
        Action<RabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.RemoveAll<IQueueProvider>();
        services.AddSingleton<IQueueProvider>(sp =>
        {
            var opts = new RabbitMqOptions();
            configure(opts);
            var logger = sp.GetService(typeof(Microsoft.Extensions.Logging.ILogger<RabbitMqQueueProvider>))
                as Microsoft.Extensions.Logging.ILogger<RabbitMqQueueProvider>;
            return new RabbitMqQueueProvider(opts, logger);
        });
        return services;
    }
}

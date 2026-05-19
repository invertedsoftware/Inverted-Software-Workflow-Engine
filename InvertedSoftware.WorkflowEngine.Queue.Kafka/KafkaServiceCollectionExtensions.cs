// Copyright (c) Inverted Software. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvertedSoftware.WorkflowEngine.Queue.Kafka;

public static class KafkaServiceCollectionExtensions
{
    public static IServiceCollection AddKafkaQueueProvider(
        this IServiceCollection services,
        Action<KafkaOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.RemoveAll<IQueueProvider>();
        services.AddSingleton<IQueueProvider>(sp =>
        {
            var opts = new KafkaOptions();
            configure(opts);
            var logger = sp.GetService(typeof(Microsoft.Extensions.Logging.ILogger<KafkaQueueProvider>))
                as Microsoft.Extensions.Logging.ILogger<KafkaQueueProvider>;
            return new KafkaQueueProvider(opts, logger);
        });
        return services;
    }
}

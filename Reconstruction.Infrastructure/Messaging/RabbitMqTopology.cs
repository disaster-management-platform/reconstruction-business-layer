using RabbitMQ.Client;

namespace Reconstruction.Infrastructure.Messaging;

public static class RabbitMqTopology
{
    public static ConnectionFactory CreateFactory(RabbitMqOptions options) => new()
    {
        HostName = options.HostName, Port = options.Port, VirtualHost = options.VirtualHost,
        UserName = options.UserName, Password = options.Password, AutomaticRecoveryEnabled = false
    };

    public static IReadOnlyList<(string Queue, string Binding)> Subscriptions(RabbitMqOptions options) =>
    [
        (options.FormalizationQueue, "reconstruction.proposals.generated"),
        (options.FormalizationQueue, "reconstruction.proposals.selected"),
        (options.FormalizationQueue, "reconstruction.project.created"), // Outbox histórica.
        (options.MonitoringQueue, "reconstruction.project.formalized"),
        (options.MonitoringQueue, "reconstruction.project.budget-revised"),
        (options.ClosureQueue, "reconstruction.activity.completed"),
        (options.ClosureQueue, "reconstruction.project.closed"),
        (options.IntegrationsQueue, "reconstruction.#"),
        (options.TimelineQueue, "reconstruction.#")
    ];

    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true,
            autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Direct, durable: true,
            autoDelete: false, cancellationToken: ct);
        // Every binding exists before the first publication, even if a consumer
        // has not started. Existing durable classic queues are preserved.
        foreach (var (queue, binding) in Subscriptions(options))
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await channel.QueueBindAsync(queue, options.Exchange, binding, cancellationToken: ct);
            await channel.QueueDeclareAsync(queue + ".dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await channel.QueueBindAsync(queue + ".dlq", options.DeadLetterExchange, queue, cancellationToken: ct);
        }
    }
}

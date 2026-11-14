using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reconstruction.Application.Ports;

namespace Reconstruction.Infrastructure.Messaging.Consumers;

public sealed class MonitoringConsumer(IOptions<RabbitMqOptions> options, IServiceScopeFactory scopes,
    IEventTransport transport, ILogger<MonitoringConsumer> logger) : RabbitMqConsumer(options, scopes, transport, logger)
{
    protected override string ConsumerName => ConsumerNames.Monitoring;
    protected override string Queue(RabbitMqOptions options) => options.MonitoringQueue;
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reconstruction.Application.Ports;

namespace Reconstruction.Infrastructure.Messaging;

public sealed class RabbitMqTimelineConsumer(IOptions<RabbitMqOptions> options, IServiceScopeFactory scopes,
    IEventTransport transport, ILogger<RabbitMqTimelineConsumer> logger) : RabbitMqConsumer(options, scopes, transport, logger)
{
    protected override string ConsumerName => ConsumerNames.Timeline;
    protected override string Queue(RabbitMqOptions options) => options.TimelineQueue;
}

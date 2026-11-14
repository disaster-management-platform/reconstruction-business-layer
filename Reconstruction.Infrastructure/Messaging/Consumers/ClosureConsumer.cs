using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reconstruction.Application.Ports;

namespace Reconstruction.Infrastructure.Messaging.Consumers;

public sealed class ClosureConsumer(IOptions<RabbitMqOptions> options, IServiceScopeFactory scopes,
    IEventTransport transport, ILogger<ClosureConsumer> logger) : RabbitMqConsumer(options, scopes, transport, logger)
{
    protected override string ConsumerName => ConsumerNames.Closure;
    protected override string Queue(RabbitMqOptions options) => options.ClosureQueue;
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reconstruction.Application.Ports;

namespace Reconstruction.Infrastructure.Messaging.Consumers;

public sealed class FormalizationConsumer(IOptions<RabbitMqOptions> options, IServiceScopeFactory scopes,
    IEventTransport transport, ILogger<FormalizationConsumer> logger) : RabbitMqConsumer(options, scopes, transport, logger)
{
    protected override string ConsumerName => ConsumerNames.Formalization;
    protected override string Queue(RabbitMqOptions options) => options.FormalizationQueue;
}

using Microsoft.Extensions.DependencyInjection;
using Reconstruction.Application.Messaging;
using Reconstruction.Application.Ports;

namespace Reconstruction.Infrastructure.Messaging.Inbox;

// Adaptador de entrada: selecciona el caso de uso; las reglas quedan en Application.
public sealed class ConsumerInboxDispatcher(IServiceProvider services)
{
    public Task DispatchAsync(string consumerName, EventEnvelope envelope, CancellationToken ct) =>
        Resolve(consumerName).HandleAsync(envelope, ct);
    public Task RebuildAsync(string consumerName, EventEnvelope envelope, CancellationToken ct) =>
        Resolve(consumerName).RebuildProjectionAsync(envelope, ct);
    private ConsumerEventHandler Resolve(string consumerName) => consumerName switch
    {
        ConsumerNames.Formalization => services.GetRequiredService<FormalizationEventHandler>(),
        ConsumerNames.Monitoring => services.GetRequiredService<MonitoringEventHandler>(),
        ConsumerNames.Closure => services.GetRequiredService<ClosureEventHandler>(),
        ConsumerNames.Timeline => services.GetRequiredService<TimelineEventHandler>(),
        _ => throw new InvalidOperationException("Consumidor no registrado.")
    };
}

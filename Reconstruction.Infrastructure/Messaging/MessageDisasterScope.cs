using Reconstruction.Application.Ports;

namespace Reconstruction.Infrastructure.Messaging;

// Each trusted domain-event delivery gets a fresh dependency-injection scope.
// Projection handlers can read only the disaster carried by that event.
public sealed class MessageDisasterScope
{
    public IReadOnlyCollection<int> AllowedDisasters { get; private set; } = [];
    public void Bind(EventEnvelope envelope)
    {
        if (envelope.Id == Guid.Empty || envelope.DisasterId <= 0 || envelope.SchemaVersion != 1)
            throw new ArgumentException("Invalid domain-event scope.", nameof(envelope));
        if (AllowedDisasters.Count != 0 && !AllowedDisasters.Contains(envelope.DisasterId))
            throw new InvalidOperationException("A message scope cannot change disaster.");
        AllowedDisasters = [envelope.DisasterId];
    }
}

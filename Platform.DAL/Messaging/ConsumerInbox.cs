using Microsoft.EntityFrameworkCore;
using Platform.DAL.Contexts;
using Platform.DAL.Models;
using Reconstruction.Application.Ports;

namespace Platform.DAL.Messaging;

public sealed class ConsumerInbox(ReconstructionDbContext db) : IConsumerInbox
{
    public Task<bool> HasProcessedAsync(string consumerName, Guid eventId, CancellationToken ct) =>
        db.InboxMessages.AnyAsync(x => x.ConsumerName == consumerName && x.EventId == eventId, ct);

    public void Record(string consumerName, Guid eventId, string outcomeJson) => db.InboxMessages.Add(new InboxMessage
    {
        ConsumerName = consumerName, EventId = eventId, ProcessedAt = DateTimeOffset.UtcNow, OutcomeJson = outcomeJson
    });
}

public sealed class TimelineWriter(ReconstructionDbContext db) : ITimelineWriter
{
    public void Append(EventEnvelope envelope) => db.TimelineEntries.Add(new TimelineEntry
    {
        Id = Guid.NewGuid(), EventId = envelope.Id, DisasterId = envelope.DisasterId,
        ProjectId = envelope.ProjectId, EventType = envelope.Type,
        Description = envelope.Detail ?? "", OccurredAt = envelope.OccurredAt
    });
}

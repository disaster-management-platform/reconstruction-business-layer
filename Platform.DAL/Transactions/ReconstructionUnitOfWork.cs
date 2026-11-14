using System.Text.Json;
using Reconstruction.Application.Ports;
using Platform.DAL.Contexts;
using Platform.DAL.Models;
using Reconstruction.Application.Messaging;

namespace Platform.DAL.Transactions;

public sealed class ReconstructionUnitOfWork(ReconstructionDbContext context) : IReconstructionUnitOfWork
{
    public async Task CommitAsync(IntegrationEvent? evt, CancellationToken ct)
    {
        if (evt is not null)
        {
            var envelope = new EventEnvelope(Guid.NewGuid(), evt.Type, evt.DisasterId,
                evt.ProjectId, evt.Actor, evt.Detail, DateTimeOffset.UtcNow,
                EventPayloadJson.Serialize(evt.Data));
            context.OutboxMessages.Add(new OutboxMessage
            {
                Id = envelope.Id, EventType = envelope.Type,
                PayloadJson = JsonSerializer.Serialize(envelope),
                CreatedUnixMs = envelope.OccurredAt.ToUnixTimeMilliseconds()
            });
            context.AuditEntries.Add(new AuditEntry
            {
                Id = Guid.NewGuid(), EventId = envelope.Id, Actor = evt.Actor,
                DataJson = EventPayloadJson.Serialize(evt.Data).GetRawText(),
                BeforeJson = evt.Before is null ? null : EventPayloadJson.Serialize(evt.Before).GetRawText(),
                AfterJson = evt.After is null ? null : EventPayloadJson.Serialize(evt.After).GetRawText(),
                Action = evt.Type, Detail = evt.Detail, DisasterId = evt.DisasterId,
                ProjectId = evt.ProjectId, OccurredAt = envelope.OccurredAt
            });
        }
        // SaveChanges ejecuta inserts/updates + Outbox + auditoría en una misma transacción local.
        await context.SaveChangesAsync(ct);
    }
}

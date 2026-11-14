using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.DAL.Contexts;
using Reconstruction.Application.Ports;

namespace Platform.DAL.Messaging;

public sealed class WorkflowReplaySource(ReconstructionDbContext db) : IWorkflowReplaySource
{
    public async Task<IReadOnlyList<EventEnvelope>> ReadPublishedAsync(CancellationToken ct)
    {
        var payloads = await db.OutboxMessages.AsNoTracking().Where(x => x.SentAt != null
            && (x.EventType == "reconstruction.proposals.generated" || x.EventType == "reconstruction.proposals.selected"
                || x.EventType == "reconstruction.project.created" || x.EventType == "reconstruction.project.formalized" || x.EventType == "reconstruction.project.budget-revised"
                || x.EventType == "reconstruction.activity.completed" || x.EventType == "reconstruction.project.closed"))
            .Select(x => x.PayloadJson).ToListAsync(ct);
        return payloads.Select(x => JsonSerializer.Deserialize<EventEnvelope>(x,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException("Evento histórico vacío."))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToArray();
    }
}

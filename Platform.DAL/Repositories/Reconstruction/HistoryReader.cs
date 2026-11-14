using Microsoft.EntityFrameworkCore;
using Reconstruction.Application.Ports;
using Platform.DAL.Contexts;

namespace Platform.DAL.Repositories.Reconstruction;

public sealed class HistoryReader(ReconstructionDbContext db, IDisasterAccess access) : IReconstructionHistoryReader
{
    private static System.Text.Json.JsonElement? Parse(string? json) => json is null ? null
        : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
    public async Task<IReadOnlyList<HistoryRecord>> GetAuditAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.AuditEntries.AsNoTracking().Where(x => x.ProjectId == id && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(x.DisasterId)))
            .ToListAsync(ct);
        return rows.OrderBy(x => x.OccurredAt).Select(x => new HistoryRecord(x.EventId, x.Action, x.Detail,
            x.OccurredAt, x.Actor, Parse(x.DataJson), Parse(x.BeforeJson), Parse(x.AfterJson))).ToArray();
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetTimelineAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.TimelineEntries.AsNoTracking().Where(x => x.ProjectId == id && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(x.DisasterId)))
            .Select(x => new HistoryRecord(x.EventId, x.EventType, x.Description, x.OccurredAt)).ToListAsync(ct);
        return rows.OrderBy(x => x.OccurredAt).ToArray();
    }
}

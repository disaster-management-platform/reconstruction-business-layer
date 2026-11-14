using DisasterLocation.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.DAL.Contexts;

namespace Platform.DAL.Repositories.Disasters;

public sealed class DisasterRepository(DisasterDbContext context) : IDisasterRepository
{
    public async Task<IReadOnlyList<Disaster>> GetAllAsync(CancellationToken ct = default) =>
        await context.Disasters.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(ct);
    public Task<Disaster?> GetByIdAsync(int id, CancellationToken ct = default) =>
        context.Disasters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Disaster?> GetByClientRequestIdAsync(Guid id, CancellationToken ct = default) =>
        context.Disasters.AsNoTracking().SingleOrDefaultAsync(x => x.ClientRequestId == id, ct);

    public async Task<bool> ChangePhaseAsync(int id, DisasterPhase previous, DisasterPhase phase, long expectedVersion,
        string actor, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var affected = await context.Disasters.Where(d => d.Id == id && d.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Phase, phase).SetProperty(d => d.Version, expectedVersion + 1), ct);
        if (affected == 0) return false;
        context.PhaseAudit.Add(new Platform.DAL.Models.DisasterPhaseAudit { Id = Guid.NewGuid(), DisasterId = id,
            Before = previous, After = phase, Version = expectedVersion + 1, Actor = actor, OccurredAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<DisasterSaveResult> AddAsync(Disaster disaster, CancellationToken ct = default)
    {
        if (disaster.ClientRequestId is Guid requestId)
        {
            var existing = await GetByClientRequestIdAsync(requestId, ct);
            if (existing is not null) return new DisasterSaveResult(existing, false);
        }
        context.Disasters.Add(disaster);
        try
        {
            await context.SaveChangesAsync(ct);
            return new DisasterSaveResult(disaster, true);
        }
        catch (DbUpdateException ex) when (disaster.ClientRequestId.HasValue &&
            ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            context.Entry(disaster).State = EntityState.Detached;
            var existing = await GetByClientRequestIdAsync(disaster.ClientRequestId.Value, ct);
            if (existing is null) throw;
            return new DisasterSaveResult(existing, false);
        }
    }
}

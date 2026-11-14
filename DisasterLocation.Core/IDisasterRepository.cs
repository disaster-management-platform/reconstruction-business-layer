namespace DisasterLocation.Core;

public interface IDisasterRepository
{
    Task<IReadOnlyList<Disaster>> GetAllAsync(CancellationToken ct = default);
    Task<Disaster?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Disaster?> GetByClientRequestIdAsync(Guid clientRequestId, CancellationToken ct = default);
    Task<bool> ChangePhaseAsync(int id, DisasterPhase previous, DisasterPhase phase, long expectedVersion,
        string actor, CancellationToken ct);
    Task<DisasterSaveResult> AddAsync(Disaster disaster, CancellationToken ct = default);
}
public sealed record DisasterSaveResult(Disaster Disaster, bool Created);

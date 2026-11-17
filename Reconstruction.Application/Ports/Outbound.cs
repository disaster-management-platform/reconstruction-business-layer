using Reconstruction.Domain.Models;

namespace Reconstruction.Application.Ports;

public interface IDisasterReader
{
    Task<DisasterSnapshot?> GetAsync(int disasterId, CancellationToken ct);
}
public sealed record DisasterSnapshot(int Id, int Severity, DisasterLocation.Core.DisasterPhase Phase = DisasterLocation.Core.DisasterPhase.Registered);
public interface IDamageAssessmentReader
{
    Task<IReadOnlyList<DamageAssessment>> GetForDisasterAsync(int disasterId, CancellationToken ct);
}
public interface IFundingReader
{
    // Saldo de la fuente externa antes de descontar las reservas locales de reconstrucción.
    Task<FundingAvailability> GetAvailableAsync(int disasterId, CancellationToken ct);
}

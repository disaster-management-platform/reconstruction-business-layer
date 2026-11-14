using Reconstruction.Application.Ports;
using Reconstruction.Domain;

namespace Reconstruction.Application.Services;

public sealed record FundingDto(int DisasterId, string SourceId, string Source, string Currency,
    decimal AllocatedAmount, decimal CommittedAmount, decimal AvailableAmount);
public sealed class FundingService(IFundingReader funding, IFundingReservationRepository reservations, IProjectRepository projects)
{
    public async Task<FundingDto> GetAsync(int disasterId, CancellationToken ct)
    {
        if (disasterId <= 0) throw new DomainException("Identificador de desastre inválido.");
        var allocation = await funding.GetAvailableAsync(disasterId, ct);
        if (allocation.DisasterId != disasterId || allocation.AvailableAmount < 0)
            throw new DomainException("La asignación de donaciones no corresponde al desastre.");
        var reservation = await reservations.FindAsync(disasterId, ct);
        var committed = reservation?.ReservedAmount ?? (await projects.ListAsync(disasterId, ct))
            .Sum(p => p.Status == ProjectStatus.Closed ? p.Activities.Sum(a => a.GetActualCost()) : p.FinalBudget);
        return new(disasterId, allocation.SourceId, allocation.Source, allocation.Currency,
            allocation.AvailableAmount, committed, Math.Max(0m, allocation.AvailableAmount - committed));
    }
}

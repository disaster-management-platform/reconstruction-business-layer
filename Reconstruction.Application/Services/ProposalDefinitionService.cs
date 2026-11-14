using Reconstruction.Application.Ports;
using Reconstruction.Application.Contracts;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;

namespace Reconstruction.Application.Services;

public sealed class ProposalDefinitionService(IProposalRepository proposals, ProposalReconciliationService reconciliation)
{
    public async Task<IReadOnlyList<ProposalDto>> GenerateAsync(int disasterId, string actor, CancellationToken ct) =>
        Ordered(await reconciliation.ReconcileAsync(disasterId, actor, ct));

    public async Task<IReadOnlyList<ProposalDto>> ListAsync(int disasterId, CancellationToken ct)
    {
        if (disasterId <= 0) throw new DomainException("El identificador de desastre debe ser positivo.");
        return Ordered(await proposals.ForDisasterAsync(disasterId, ct));
    }

    private static IReadOnlyList<ProposalDto> Ordered(IEnumerable<ReconstructionProposal> proposals) =>
        proposals.Where(p => p.Status != ProposalStatus.Obsolete)
            .OrderByDescending(p => p.AverageSeverity).ThenByDescending(p => p.ProposedBudget)
            .ThenBy(p => p.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.AssetType, StringComparer.OrdinalIgnoreCase).Select(p => p.ToDto()).ToArray();
}

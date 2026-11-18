using Reconstruction.Application.Messaging;
using Reconstruction.Application.Ports;
using DisasterLocation.Core;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Domain.Policies;

namespace Reconstruction.Application.Services;

// Regla compartida por la consulta de propuestas y la creación de proyectos.
public sealed class ProposalReconciliationService(IDisasterReader disasters, IDamageAssessmentReader assessments,
    IProposalRepository proposals, IProposalPolicy policy, IProjectRepository projects, IReconstructionUnitOfWork work,
    IFundingReader funding, IFundingReservationRepository reservations)
{
    public async Task<IReadOnlyList<ReconstructionProposal>> ReconcileAsync(int disasterId, string actor, CancellationToken ct)
    {
        if (disasterId <= 0) throw new DomainException("El identificador de desastre debe ser positivo.");
        var disaster = await disasters.GetAsync(disasterId, ct)
            ?? throw new MissingResourceException("Desastre no encontrado.");
        if (disaster.Phase is not (DisasterPhase.Recovery or DisasterPhase.Reconstruction))
            throw new BusinessConflictException("La reconstrucción requiere que haya finalizado la fase de respuesta.");
        var damage = await assessments.GetForDisasterAsync(disasterId, ct);

        if (damage.GroupBy(e => e.EvaluationId).Any(group => group.Count() > 1))
            throw new DomainException("La fuente contiene identificadores de evaluación duplicados.");
        var existing = await proposals.ForDisasterAsync(disasterId, ct);
        var selected = existing.Where(p => p.Status == ProposalStatus.Selected).ToArray();
        var reserved = selected.SelectMany(p => p.GetEvaluations()).Select(e => e.EvaluationId).ToHashSet();
        var reservedAssets = selected.SelectMany(p => p.Evaluations).Where(e => e.AssetId is not null).Select(e => e.AssetId!).ToHashSet(StringComparer.Ordinal);
        var financing = await funding.GetAvailableAsync(disasterId, ct);
        if (financing.DisasterId != disasterId || financing.AvailableAmount < 0)
            throw new DomainException("La disponibilidad de donaciones no corresponde al desastre.");
        var held = await reservations.FindAsync(disasterId, ct);
        var currentProjects = await projects.ListAsync(disasterId, ct);
        var committed = held?.ReservedAmount ?? currentProjects.Sum(p => p.Status == ProjectStatus.Closed ? p.Activities.Sum(a => a.GetActualCost()) : p.FinalBudget);
        var pendingEstimates = selected.Where(p => currentProjects.Any(project => project.ProposalId == p.Id && project.FinalBudget == 0))
            .Sum(p => p.ProposedBudget);
        var classified = policy.Generate(disasterId, damage.Where(e => !reserved.Contains(e.EvaluationId) && (e.AssetId is null || !reservedAssets.Contains(e.AssetId))).ToArray(),
            decimal.Floor(Math.Max(0m, financing.AvailableAmount - committed - pendingEstimates) * 100m) / 100m);
        var reviews = new List<ProjectEvaluationReviewData>();
        var changed = 0;
        foreach (var proposal in selected)
        {
            var original = proposal.GetEvaluations();
            var ids = original.Select(e => e.EvaluationId).ToHashSet();
            var assets = original.Where(e => e.AssetId is not null).Select(e => e.AssetId!).ToHashSet(StringComparer.Ordinal);
            var current = damage.Where(e => ids.Contains(e.EvaluationId) || (e.AssetId is not null && assets.Contains(e.AssetId))).ToArray();
            if (!proposal.ReconcileProjectEvaluations(current)) continue;
            changed++;
            var project = await projects.ByProposalAsync(proposal.Id, ct);
            if (project is not null)
                reviews.Add(new ProjectEvaluationReviewData(project.Id, proposal.Id, proposal.ReviewFingerprint.Length > 0,
                    proposal.ReviewFingerprint, original, current, "EvaluationsChanged"));
        }
        var available = new List<ReconstructionProposal>();
        var added = new List<ReconstructionProposal>();
        foreach (var group in classified)
        {
            var evaluations = group.GetEvaluations().Where(e => !reserved.Contains(e.EvaluationId)).ToArray();
            if (evaluations.Length == 0) continue;
            var proposal = existing.FirstOrDefault(p => p.Status == ProposalStatus.Proposed && p.HasSameClassification(group));
            if (proposal is null)
            {
                proposal = new ReconstructionProposal(disasterId, group.Zone, group.AssetType, group.Damage, evaluations, group.ProposedBudget);
                added.Add(proposal); changed++;
            }
            else
            {
                if (proposal.RefreshEvaluations(evaluations)) changed++;
                if (proposal.SetProposedBudget(group.ProposedBudget)) changed++;
            }
            available.Add(proposal);
        }
        var currentIds = available.Select(p => p.Id).ToHashSet();
        foreach (var obsolete in existing.Where(p => p.Status == ProposalStatus.Proposed && !currentIds.Contains(p.Id)))
            if (obsolete.MarkObsolete()) changed++;
        if (changed > 0)
        {
            proposals.AddRange(added);
            await work.CommitAsync(IntegrationEvent.Typed(disasterId, null, actor,
                new ProposalsGeneratedEvent(available.Select(p => new ProposalReviewData(p.Id, p.Zone, p.AssetType,
                    p.Damage, p.ProposedBudget, p.AssessmentCount)).ToArray(), reviews)), ct);
        }
        return existing.Concat(added).ToArray();
    }
}

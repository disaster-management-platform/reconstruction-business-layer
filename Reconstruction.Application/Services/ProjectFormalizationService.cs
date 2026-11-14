using Reconstruction.Application.Contracts;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Application.Messaging;

namespace Reconstruction.Application.Services;

public sealed class ProjectFormalizationService(
    IProposalRepository proposals, IProjectRepository projects, IReconstructionUnitOfWork work,
    IFundingReader funding, IFundingReservationRepository reservations, ProposalReconciliationService reconciliation)
{
    public async Task<ProjectDto> CreateFromProposalAsync(Guid proposalId, long expectedVersion, string actor, CancellationToken ct)
    {
        if (proposalId == Guid.Empty) throw new DomainException("La propuesta es obligatoria.");
        var existing = await projects.ByProposalAsync(proposalId, ct);
        if (existing is not null) return existing.ToDto();
        var proposal = await proposals.FindAsync(proposalId, ct)
            ?? throw new MissingResourceException("Propuesta no encontrada.");
        ProjectVersionGuard.EnsureCurrent(proposal.Version, expectedVersion);
        if (proposal.Status != ProposalStatus.Proposed)
            throw new BusinessConflictException("La propuesta no está vigente para crear un proyecto.");
        await reconciliation.ReconcileAsync(proposal.DisasterId, actor, ct);
        if (proposal.Status != ProposalStatus.Proposed)
            throw new BusinessConflictException("La propuesta dejó de estar vigente según las evaluaciones actuales.");
        ProjectVersionGuard.EnsureCurrent(proposal.Version, expectedVersion);
        var project = new ReconstructionProject(proposal);
        proposal.Select();
        projects.Add(project);
        await work.CommitAsync(IntegrationEvent.Typed(project.DisasterId, project.Id, actor,
            new ProposalsSelectedEvent(project.Id, proposal.Id, project.Version)), ct);
        return project.ToDto();
    }

    public async Task<ProjectDto> FormalizeAsync(Guid projectId, FormalizeProjectRequest request,
        string actor, CancellationToken ct)
    {
        var project = await projects.FindAsync(projectId, ct)
            ?? throw new MissingResourceException("Proyecto no encontrado.");
        ProjectVersionGuard.EnsureCurrent(project.Version, request.ExpectedVersion);
        var financing = await funding.GetAvailableAsync(project.DisasterId, ct);
        if (financing.DisasterId != project.DisasterId)
            throw new DomainException("La financiación no corresponde al desastre del proyecto.");
        if (string.IsNullOrWhiteSpace(financing.Source) || request.FundingSource != financing.Source || request.FundingSourceId != financing.SourceId)
            throw new DomainException("Seleccione la fuente de donaciones ofrecida por el módulo financiero.");
        var reservation = await reservations.FindAsync(project.DisasterId, ct);
        var isNew = reservation is null;
        reservation ??= new DisasterFundingReservation(project.DisasterId,
            (await projects.ListAsync(project.DisasterId, ct)).Sum(p => p.Status == ProjectStatus.Closed ? p.Activities.Sum(a => a.GetActualCost()) : p.FinalBudget));
        var before = project.ToDto();
        var previousBudget = project.FinalBudget;
        reservation.EnsureAvailable(previousBudget, request.FinalBudget, financing.AvailableAmount);
        project.SaveFormalization(request.FinalBudget, request.FundingSource, request.Responsible,
            request.Contractor, request.Activities ?? [], financing.SourceId);
        reservation.Reserve(previousBudget, project.FinalBudget, financing.AvailableAmount);
        if (isNew) reservations.Add(reservation);
        await work.CommitAsync(IntegrationEvent.Typed(project.DisasterId, project.Id, actor,
            new FormalizationSavedEvent(project.Id, project.Version, project.FinalBudget,
                project.FundingSource, project.Responsible, project.Contractor), before, project.ToDto()), ct);
        return project.ToDto();
    }

    public async Task<ProjectDto> ConfirmAsync(Guid projectId, long expectedVersion, string actor, CancellationToken ct)
    {
        var project = await projects.FindAsync(projectId, ct) ?? throw new MissingResourceException("Proyecto no encontrado.");
        ProjectVersionGuard.EnsureCurrent(project.Version, expectedVersion);
        var before = project.ToDto();
        if (project.ConfirmFormalization())
            await work.CommitAsync(IntegrationEvent.Typed(project.DisasterId, project.Id, actor,
                new ProjectFormalizedEvent(project.Id, project.Version, project.FinalBudget, "", null,
                    project.Activities.Select(a => new ScheduledActivityData(a.Id, a.Name, a.Responsible,
                        a.StartsAt, a.DueAt, a.Budget)).ToArray()), before, project.ToDto()), ct);
        return project.ToDto();
    }

    public async Task<ProjectDto> ReviseBudgetAsync(Guid projectId, ReviseBudgetRequest request, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1000)
            throw new DomainException("Indique el motivo del cambio de presupuesto (máximo 1000 caracteres).");
        var project = await projects.FindAsync(projectId, ct) ?? throw new MissingResourceException("Proyecto no encontrado.");
        ProjectVersionGuard.EnsureCurrent(project.Version, request.ExpectedVersion);
        var allocation = await funding.GetAvailableAsync(project.DisasterId, ct);
        if (allocation.DisasterId != project.DisasterId || allocation.SourceId != project.FundingSourceId)
            throw new DomainException("La asignación financiera ya no corresponde a la fuente del proyecto.");
        var reservation = await reservations.FindAsync(project.DisasterId, ct)
            ?? throw new BusinessConflictException("La reserva financiera del proyecto debe ser conciliada.");
        var before = project.ToDto();
        var previousBudget = project.FinalBudget;
        reservation.EnsureAvailable(previousBudget, request.Budget, allocation.AvailableAmount);
        project.ReviseBudget(request.Budget, request.ActivityBudgets);
        reservation.Reserve(previousBudget, request.Budget, allocation.AvailableAmount);
        await work.CommitAsync(IntegrationEvent.Typed(project.DisasterId, project.Id, actor,
            new ProjectBudgetRevisedEvent(project.Id, project.Version, previousBudget, project.FinalBudget, request.Reason.Trim()),
            before, project.ToDto()), ct);
        return project.ToDto();
    }

    public async Task<ProjectDto> RegisterSignedContractAsync(Guid projectId, SignedContractRequest request,
        string actor, CancellationToken ct)
    {
        var project = await projects.FindAsync(projectId, ct)
            ?? throw new MissingResourceException("Proyecto no encontrado.");
        ProjectVersionGuard.EnsureCurrent(project.Version, request.ExpectedVersion);
        var before = project.ToDto();
        if (project.RegisterSignedContract(request.ContractReference, request.SignedAt, actor))
            await work.CommitAsync(IntegrationEvent.Typed(project.DisasterId, project.Id, actor,
                new ProjectFormalizedEvent(project.Id, project.Version, project.FinalBudget,
                    project.SignedContractReference, project.ContractSignedAt!.Value,
                    project.Activities.Select(a => new ScheduledActivityData(a.Id, a.Name, a.Responsible,
                        a.StartsAt, a.DueAt, a.Budget)).ToArray()), before, project.ToDto()), ct);
        return project.ToDto();
    }

}

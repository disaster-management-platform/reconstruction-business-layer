using System.Text.Json;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;

namespace Reconstruction.Application.Messaging;

// Proyección empresarial + Inbox se confirman juntas. Ningún consumidor ejecuta
// decisiones del usuario ni modifica el estado de los proyectos.
public abstract class ConsumerEventHandler(IConsumerInbox inbox, IReconstructionUnitOfWork work)
{
    protected abstract string ConsumerName { get; }
    protected abstract bool Accepts(string eventType);
    protected abstract Task ProcessAsync(EventEnvelope envelope, CancellationToken ct);

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        Validate(envelope);
        if (await inbox.HasProcessedAsync(ConsumerName, envelope.Id, ct)) return;
        await ProcessAsync(envelope, ct);
        inbox.Record(ConsumerName, envelope.Id, JsonSerializer.Serialize(new
        {
            envelope.Type, envelope.DisasterId, envelope.ProjectId, envelope.OccurredAt, Applied = true
        }));
        await work.CommitAsync(null, ct);
    }

    // Reconstruye únicamente la proyección desde una Outbox ya publicada. No
    // modifica la Inbox histórica ni vuelve a publicar eventos de negocio.
    public async Task RebuildProjectionAsync(EventEnvelope envelope, CancellationToken ct)
    {
        Validate(envelope);
        await ProcessAsync(envelope, ct);
        await work.CommitAsync(null, ct);
    }
    private void Validate(EventEnvelope envelope)
    {
        if (envelope.Id == Guid.Empty || envelope.DisasterId <= 0 || envelope.OccurredAt == default
            || envelope.SchemaVersion != 1 || string.IsNullOrWhiteSpace(envelope.Type) || !Accepts(envelope.Type))
            throw new DomainException("El evento recibido no es válido para este consumidor.");
    }

    protected static async Task<ReconstructionProject> ProjectAsync(IProjectRepository projects, EventEnvelope envelope, CancellationToken ct)
    {
        var project = envelope.ProjectId.HasValue ? await projects.FindAsync(envelope.ProjectId.Value, ct) : null;
        if (project is null || project.DisasterId != envelope.DisasterId)
            throw new MissingResourceException("El proyecto del evento no existe o corresponde a otro desastre.");
        return project;
    }
    protected static void ValidateProject(ReconstructionProject project, Guid id, long version)
    {
        if (id != project.Id || version <= 0 || version > project.Version)
            throw new DomainException("El proyecto o la versión del payload no coinciden con el evento.");
    }
}

public sealed class FormalizationEventHandler(IProposalRepository proposals, IProjectRepository projects,
    WorkflowService workflow, IConsumerInbox inbox, IReconstructionUnitOfWork work) : ConsumerEventHandler(inbox, work)
{
    protected override string ConsumerName => ConsumerNames.Formalization;
    protected override bool Accepts(string type) => type is "reconstruction.proposals.generated"
        or "reconstruction.proposals.selected" or "reconstruction.project.created";
    protected override async Task ProcessAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Type == "reconstruction.proposals.generated")
        {
            var available = await proposals.ForDisasterAsync(envelope.DisasterId, ct);
            var payload = envelope.Read<ProposalsGeneratedEvent>();
            var entries = payload is null ? available.Select(ReviewData).ToArray() : payload.Proposals;
            if (entries is null || (payload is null && entries.Count == 0)) throw new MissingResourceException("No se encontraron las propuestas del evento.");
            if (entries.Any(x => x is null || x.ProposalId == Guid.Empty || x.ProposedBudget < 0 || x.AssessmentCount < 1)
                || entries.Select(x => x.ProposalId).Distinct().Count() != entries.Count)
                throw new DomainException("El payload de propuestas no es válido.");
            foreach (var entry in entries)
            {
                var proposal = available.SingleOrDefault(p => p.Id == entry.ProposalId)
                    ?? throw new MissingResourceException("Una propuesta del evento no pertenece al desastre.");
                await workflow.PutAsync(WorkflowKind.ProposalReview, proposal.Id, null, proposal.Id, envelope,
                    proposal.Status == ProposalStatus.Proposed, ReviewData(proposal), ct);
            }
            if (payload is not null)
            {
                foreach (var obsolete in available.Where(p => entries.All(e => e.ProposalId != p.Id)))
                    await workflow.ResolveAsync(WorkflowKind.ProposalReview, obsolete.Id, envelope, ct);
                foreach (var review in payload.ProjectReviews ?? [])
                {
                    var proposal = available.SingleOrDefault(p => p.Id == review.ProposalId && p.Status == ProposalStatus.Selected)
                        ?? throw new DomainException("La revisión no corresponde a una propuesta con proyecto.");
                    var project = await projects.ByProposalAsync(proposal.Id, ct);
                    if (project is null || project.Id != review.ProjectId)
                        throw new DomainException("El proyecto de la solicitud de revisión no corresponde a la propuesta.");
                    if (proposal.ReviewFingerprint != review.Fingerprint) continue; // Evento anterior a otra reconciliación.
                    if (review.RequiresReview != (proposal.ReviewFingerprint.Length > 0))
                        throw new DomainException("El estado de revisión no corresponde a las evaluaciones reconciliadas.");
                    await workflow.PutAsync(WorkflowKind.ProjectReviewRequested, project.Id, project.Id, proposal.Id,
                        envelope, review.RequiresReview, review, ct);
                }
            }
            return;
        }
        var selection = envelope.Read<ProposalsSelectedEvent>();
        // Compatibilidad con eventos históricos de selección que no tenían ProjectId:
        // solo se preparan proyectos reales; jamás se inventa un destino leyendo Detail.
        var targets = envelope.ProjectId.HasValue
            ? new[] { await ProjectAsync(projects, envelope, ct) }
            : selection is null ? await projects.ListAsync(envelope.DisasterId, ct) : throw new DomainException("Falta el proyecto seleccionado.");
        foreach (var project in targets)
        {
            if (selection is not null)
            {
                ValidateProject(project, selection.ProjectId, selection.ProjectVersion);
                if (selection.ProposalId != project.ProposalId) throw new DomainException("La propuesta no corresponde al proyecto seleccionado.");
            }
            await workflow.ResolveAsync(WorkflowKind.ProposalReview, project.ProposalId, envelope, ct);
            await workflow.PutAsync(WorkflowKind.FormalizationRequest, project.Id, project.Id, project.ProposalId,
                envelope, project.Status is ProjectStatus.Draft or ProjectStatus.Formalized,
                new ProposalsSelectedEvent(project.Id, project.ProposalId, project.Version), ct);
        }
    }
    private static ProposalReviewData ReviewData(ReconstructionProposal p) =>
        new(p.Id, p.Zone, p.AssetType, p.Damage, p.ProposedBudget, p.AssessmentCount);
}

public sealed class MonitoringEventHandler(IProjectRepository projects, WorkflowService workflow,
    IConsumerInbox inbox, IReconstructionUnitOfWork work) : ConsumerEventHandler(inbox, work)
{
    protected override string ConsumerName => ConsumerNames.Monitoring;
    protected override bool Accepts(string type) => type is "reconstruction.project.formalized" or "reconstruction.project.budget-revised";
    protected override async Task ProcessAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var project = await ProjectAsync(projects, envelope, ct);
        if (envelope.Type == "reconstruction.project.budget-revised")
        {
            var revised = envelope.Read<ProjectBudgetRevisedEvent>() ?? throw new DomainException("Falta el cambio de presupuesto.");
            ValidateProject(project, revised.ProjectId, revised.ProjectVersion);
            await workflow.PutAsync(WorkflowKind.MonitoringPrepared, project.Id, project.Id, project.ProposalId,
                envelope, project.Status == ProjectStatus.Monitoring,
                new ProjectFormalizedEvent(project.Id, project.Version, project.FinalBudget, project.SignedContractReference,
                    project.ContractSignedAt, project.Activities.Select(a => new ScheduledActivityData(a.Id, a.Name,
                        a.Responsible, a.StartsAt, a.DueAt, a.Budget)).ToArray()), ct);
            return;
        }
        var payload = envelope.Read<ProjectFormalizedEvent>();
        if (project.Status is ProjectStatus.Draft or ProjectStatus.Formalized)
        {
            if (payload is not null) throw new DomainException("El proyecto aún no tiene un contrato firmado registrado.");
            // Un Formalized histórico anterior al registro de contratos todavía
            // necesita la decisión del usuario; no habilita monitoreo.
            await workflow.PutAsync(WorkflowKind.FormalizationRequest, project.Id, project.Id, project.ProposalId,
                envelope, true, new ProposalsSelectedEvent(project.Id, project.ProposalId, project.Version), ct);
            return;
        }
        if (payload is not null)
        {
            ValidateProject(project, payload.ProjectId, payload.ProjectVersion);
            if (payload.Activities is null || payload.Activities.Any(a => a is null)
                || !payload.Activities.Select(a => a.ActivityId).Order().SequenceEqual(project.Activities.Select(a => a.Id).Order())
                || (payload.ProjectVersion == project.Version && payload.FinalBudget != project.FinalBudget)
                || payload.ContractReference != project.SignedContractReference
                || payload.SignedAt != project.ContractSignedAt)
                throw new DomainException("El payload no corresponde a la formalización del proyecto.");
            foreach (var scheduled in payload.Activities)
            {
                var actual = project.Activities.Single(a => a.Id == scheduled.ActivityId);
                if (scheduled.Name != actual.Name || scheduled.Responsible != actual.Responsible
                    || scheduled.StartsAt != actual.StartsAt || scheduled.DueAt != actual.DueAt || (payload.ProjectVersion == project.Version && scheduled.Budget != actual.Budget))
                    throw new DomainException("El cronograma del evento no coincide con el proyecto.");
            }
        }
        payload ??= new ProjectFormalizedEvent(project.Id, project.Version, project.FinalBudget,
            project.SignedContractReference, project.ContractSignedAt,
            project.Activities.Select(a => new ScheduledActivityData(a.Id, a.Name, a.Responsible, a.StartsAt, a.DueAt, a.Budget)).ToArray());
        await workflow.ResolveAsync(WorkflowKind.FormalizationRequest, project.Id, envelope, ct);
        await workflow.PutAsync(WorkflowKind.MonitoringPrepared, project.Id, project.Id, project.ProposalId,
            envelope, project.Status == ProjectStatus.Monitoring, payload, ct);
    }
}

public sealed class ClosureEventHandler(IProjectRepository projects, WorkflowService workflow,
    IConsumerInbox inbox, IReconstructionUnitOfWork work) : ConsumerEventHandler(inbox, work)
{
    protected override string ConsumerName => ConsumerNames.Closure;
    protected override bool Accepts(string type) => type is "reconstruction.activity.completed" or "reconstruction.project.closed";
    protected override async Task ProcessAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var project = await ProjectAsync(projects, envelope, ct);
        if (envelope.Type == "reconstruction.activity.completed")
        {
            var completed = envelope.Read<ActivityCompletedEvent>();
            if (completed is not null)
            {
                ValidateProject(project, completed.ProjectId, completed.ProjectVersion);
                var report = project.Activities.SingleOrDefault(a => a.Id == completed.ActivityId)?.Reports
                    .SingleOrDefault(r => r.Id == completed.ReportId);
                if (report is null || report.Status != ActivityStatus.Completed || !report.HasRequiredEvidence
                    || report.ActualCost != completed.ActualCost)
                    throw new DomainException("El evento de actividad no corresponde a un reporte final completo del proyecto.");
            }
            await workflow.PutAsync(WorkflowKind.ClosureAvailable, project.Id, project.Id, project.ProposalId,
                envelope, project.CanFinalize(), new ClosureAvailableData(project.Id, project.Version,
                    project.Activities.Count(a => a.IsCompleteWithEvidence), project.Activities.Count), ct);
            return;
        }
        var closed = envelope.Read<ProjectClosedEvent>();
        if (closed is not null) ValidateProject(project, closed.ProjectId, closed.ProjectVersion);
        if (project.Status != ProjectStatus.Closed || project.ClosedAt is null)
            throw new DomainException("El evento de cierre requiere un proyecto finalizado.");
        closed ??= new ProjectClosedEvent(project.Id, project.Version, project.ClosedAt.Value, project.FinalBudget,
            project.Activities.Sum(a => a.GetActualCost()), project.Activities.Count, project.Activities.Sum(a => a.Reports.Count));
        if (closed.ClosedAt != project.ClosedAt || closed.FinalBudget != project.FinalBudget
            || closed.TotalExecuted != project.Activities.Sum(a => a.GetActualCost())
            || closed.TotalActivities != project.Activities.Count || closed.TotalReports != project.Activities.Sum(a => a.Reports.Count))
            throw new DomainException("El payload de cierre no corresponde al reporte final.");
        foreach (var kind in new[] { WorkflowKind.FormalizationRequest, WorkflowKind.MonitoringPrepared, WorkflowKind.ClosureAvailable })
            await workflow.ResolveAsync(kind, project.Id, envelope, ct);
        await workflow.ResolveAsync(WorkflowKind.ProposalReview, project.ProposalId, envelope, ct);
        await workflow.PutAsync(WorkflowKind.ProjectClosed, project.Id, project.Id, project.ProposalId, envelope, true, closed, ct);
    }
}

public sealed class TimelineEventHandler(ITimelineWriter timeline, IConsumerInbox inbox,
    IReconstructionUnitOfWork work) : ConsumerEventHandler(inbox, work)
{
    protected override string ConsumerName => ConsumerNames.Timeline;
    protected override bool Accepts(string type) => type.StartsWith("reconstruction.", StringComparison.Ordinal);
    protected override Task ProcessAsync(EventEnvelope envelope, CancellationToken ct)
    {
        timeline.Append(envelope);
        return Task.CompletedTask;
    }
}

namespace Reconstruction.Domain.Models;

public enum WorkflowKind { ProposalReview, FormalizationRequest, MonitoringPrepared, ClosureAvailable, ProjectClosed, ProjectReviewRequested }

// Proyección de trabajo y avisos. No reemplaza el estado del proyecto o la propuesta.
public sealed class WorkflowItem
{
    private WorkflowItem() { }
    public WorkflowKind Kind { get; private set; }
    public Guid SubjectId { get; private set; }
    public int DisasterId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? ProposalId { get; private set; }
    public bool IsActive { get; private set; }
    public string DataJson { get; private set; } = "{}";
    public Guid SourceEventId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }

    public WorkflowItem(WorkflowKind kind, Guid subjectId, int disasterId, Guid? projectId, Guid? proposalId)
    {
        if (!Enum.IsDefined(kind) || subjectId == Guid.Empty || disasterId <= 0)
            throw new DomainException("Registro de trabajo inválido.");
        Kind = kind; SubjectId = subjectId; DisasterId = disasterId; ProjectId = projectId; ProposalId = proposalId;
    }

    public void Apply(Guid eventId, DateTimeOffset occurredAt, bool active, string dataJson)
    {
        if (occurredAt < UpdatedAt || eventId == SourceEventId) return;
        SourceEventId = eventId; UpdatedAt = occurredAt; IsActive = active; DataJson = dataJson; Version++;
    }
}

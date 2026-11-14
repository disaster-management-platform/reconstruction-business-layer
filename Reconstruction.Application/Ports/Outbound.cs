using Reconstruction.Domain.Models;
using Reconstruction.Application.Messaging;

namespace Reconstruction.Application.Ports;

public interface IDisasterReader
{
    Task<DisasterSnapshot?> GetAsync(int disasterId, CancellationToken ct);
}
public sealed record DisasterSnapshot(int Id, int Severity, Reconstruction.Domain.DisasterPhase Phase = Reconstruction.Domain.DisasterPhase.Registered);
public interface IDamageAssessmentReader
{
    Task<IReadOnlyList<DamageAssessment>> GetForDisasterAsync(int disasterId, CancellationToken ct);
}
public interface IFundingReader
{
    // Saldo de la fuente externa antes de descontar las reservas locales de reconstrucción.
    Task<FundingAvailability> GetAvailableAsync(int disasterId, CancellationToken ct);
}
public interface IFundingReservationRepository
{
    Task<DisasterFundingReservation?> FindAsync(int disasterId, CancellationToken ct);
    void Add(DisasterFundingReservation reservation);
}
public interface IProposalRepository
{
    Task<IReadOnlyList<ReconstructionProposal>> ForDisasterAsync(int disasterId, CancellationToken ct);
    Task<ReconstructionProposal?> FindAsync(Guid id, CancellationToken ct);
    void AddRange(IEnumerable<ReconstructionProposal> proposals);
}
public interface IProjectRepository
{
    Task<IReadOnlyList<ReconstructionProject>> ListAsync(int? disasterId, CancellationToken ct);
    Task<ReconstructionProject?> FindAsync(Guid id, CancellationToken ct);
    Task<ReconstructionProject?> ByProposalAsync(Guid proposalId, CancellationToken ct);
    void Add(ReconstructionProject project);
}
public interface IReconstructionUnitOfWork
{
    // Agrega Outbox y auditoría a la misma transacción de escritura.
    Task CommitAsync(IntegrationEvent? evt, CancellationToken ct);
}
public sealed record IntegrationEvent(int DisasterId, Guid? ProjectId, string Actor, IIntegrationEventData Data, object? Before = null, object? After = null)
{
    public string Type => Data.EventType;
    // Texto de presentación para auditoría/historial; los consumidores utilizan Data.
    public string Detail => Data switch
    {
        ProposalsGeneratedEvent => "Propuestas reconciliadas con las evaluaciones actuales.",
        ProposalsSelectedEvent => "Propuesta elegida; proyecto pendiente de formalización.",
        FormalizationSavedEvent => "Acuerdo y presupuesto guardados.",
        ProjectFormalizedEvent => "Contrato firmado registrado; proyecto en monitoreo.",
        ActivityCompletedEvent => "Actividad: reporte final y costo registrados.",
        ActivityProgressEvent => "Actividad: reporte de avance y costo registrados.",
        ProjectClosedEvent => "Proyecto finalizado con reporte consolidado.",
        _ => Data.EventType
    };
    public static IntegrationEvent Typed(int disasterId, Guid? projectId, string actor, IIntegrationEventData data, object? before = null, object? after = null) =>
        new(disasterId, projectId, actor, data, before, after);
}

public sealed record HistoryRecord(Guid EventId, string Type, string Detail,
    DateTimeOffset OccurredAt, string? Actor = null, System.Text.Json.JsonElement? Data = null,
    System.Text.Json.JsonElement? Before = null, System.Text.Json.JsonElement? After = null);
public interface IReconstructionHistoryReader
{
    Task<IReadOnlyList<HistoryRecord>> GetAuditAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<HistoryRecord>> GetTimelineAsync(Guid projectId, CancellationToken ct);
}

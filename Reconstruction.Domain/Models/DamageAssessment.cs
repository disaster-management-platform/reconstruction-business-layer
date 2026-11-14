namespace Reconstruction.Domain.Models;

// Snapshot del contrato de evaluación. Location identifica la zona del activo.
// Budget es el monto de intervención informado por evaluación, no financiación asignada.
public sealed record DamageAssessment(
    Guid EvaluationId, int DisasterId, DateTimeOffset EvaluatedAt,
    string Evaluator, string Organization, string Location, string AssetType,
    DamageLevel DamageLevel, string Description, IReadOnlyList<string> Evidence,
    EvaluationStatus Status, decimal Budget = 0m, string? AssetId = null, long Revision = 1);

public sealed record FundingAvailability(int DisasterId, decimal AvailableAmount, string Source, string SourceId = "donations", string Currency = "COP");

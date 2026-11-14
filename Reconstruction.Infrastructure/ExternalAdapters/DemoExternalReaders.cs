using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;

namespace Reconstruction.Infrastructure.ExternalAdapters;

// Implementaciones explícitamente DEMO. Reemplazar por adaptadores REST reales cuando existan.
public sealed class DemoDamageAssessmentReader : IDamageAssessmentReader
{
    public Task<IReadOnlyList<DamageAssessment>> GetForDisasterAsync(int id, CancellationToken ct)
    {
        // Datos ilustrativos, nunca se persisten como evaluaciones del módulo externo.
        var evaluatedAt = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        DamageAssessment Evaluation(int number, string zone, string asset, DamageLevel level,
            string description, decimal budget, EvaluationStatus status = EvaluationStatus.Validated) => new(
                Guid.Parse($"11111111-1111-1111-1111-{number:000000000000}"), id, evaluatedAt,
                "Evaluador de demostración", "Organización de demostración", zone, asset, level,
                description, [$"demo:evidencia-{number}"], status, budget, $"demo-asset-{number}");
        IReadOnlyList<DamageAssessment> sample =
        [
            Evaluation(101, "Zona Norte", "Puente", DamageLevel.Destroyed, "Daños críticos en la estructura.", 120_000_000m),
            Evaluation(102, "Zona Norte", "Vivienda", DamageLevel.Severe, "Daños altos en muros.", 20_000_000m),
            Evaluation(103, "Zona Norte", "Vivienda", DamageLevel.Severe, "Daños altos en cubierta.", 25_000_000m),
            Evaluation(104, "Zona Sur", "Red eléctrica", DamageLevel.Moderate, "Daños en la distribución del sector.", 8_000_000m),
            Evaluation(105, "Zona Sur", "Carretera", DamageLevel.Minor, "Daños menores en la vía.", 2_000_000m),
            Evaluation(106, "Zona Este", "Escuela", DamageLevel.Destroyed, "Evaluación pendiente de validar.", 10_000_000m, EvaluationStatus.Draft)
        ];
        return Task.FromResult(sample);
    }
}
public sealed class DemoFundingReader : IFundingReader
{
    public Task<FundingAvailability> GetAvailableAsync(int id, CancellationToken ct) =>
        Task.FromResult(new FundingAvailability(id, 150_000_000m, "DEMO: financiación simulada, no asignada"));
}

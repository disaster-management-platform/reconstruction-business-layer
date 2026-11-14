using Reconstruction.Domain.Models;

namespace Reconstruction.Domain.Policies;

public interface IProposalPolicy
{
    IReadOnlyList<ReconstructionProposal> Generate(int disasterId, IReadOnlyList<DamageAssessment> assessments,
        decimal availableBudget = 0m);
}

public sealed class WeightedProposalPolicy : IProposalPolicy
{
    // Relative allocation weights: they do not represent the percentage of physical damage.
    public static decimal Weight(DamageLevel damage) => damage switch
    {
        DamageLevel.Minor => 10m, DamageLevel.Moderate => 20m,
        DamageLevel.Severe => 30m, DamageLevel.Destroyed => 40m,
        _ => 0m
    };

    public IReadOnlyList<ReconstructionProposal> Generate(int disasterId,
        IReadOnlyList<DamageAssessment> assessments, decimal availableBudget = 0m)
    {
        if (disasterId <= 0 || availableBudget < 0 || decimal.Round(availableBudget, 2) != availableBudget)
            throw new DomainException("El desastre y el presupuesto disponible deben ser válidos; se admiten dos decimales.");
        if (assessments.Any(a => a is null || a.DisasterId != disasterId || !Enum.IsDefined(a.Status)))
            throw new DomainException("Las evaluaciones deben corresponder al desastre y tener un estado válido.");
        if (assessments.GroupBy(a => a.EvaluationId).Any(g => g.Count() > 1))
            throw new DomainException("La fuente contiene identificadores de evaluación duplicados.");
        if (assessments.Any(a => a.Revision <= 0)) throw new DomainException("La revisión de una evaluación debe ser positiva.");
        var current = assessments.GroupBy(a => a.AssetId ?? a.EvaluationId.ToString("D"), StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.Max(a => a.Revision);
                var revisions = g.Where(a => a.Revision == latest).ToArray();
                if (revisions.Length != 1) throw new DomainException("Un activo tiene evaluaciones incompatibles con la misma revisión.");
                return revisions[0];
            });
        var validated = current.Where(a => a.Status == EvaluationStatus.Validated && a.DamageLevel != DamageLevel.None).ToArray();
        if (validated.Any(a => a.EvaluationId == Guid.Empty || a.EvaluatedAt == default
            || string.IsNullOrWhiteSpace(a.Evaluator) || string.IsNullOrWhiteSpace(a.Organization)
            || string.IsNullOrWhiteSpace(a.Location) || string.IsNullOrWhiteSpace(a.AssetType)
            || string.IsNullOrWhiteSpace(a.Description) || a.Evidence is null
            || a.Evidence.Any(string.IsNullOrWhiteSpace) || a.Budget < 0 || !Enum.IsDefined(a.DamageLevel)))
            throw new DomainException("Una evaluación validada contiene datos incompletos o un nivel de daño inválido.");
        var groups = validated.GroupBy(a => (Zone: Normalize(a.Location), Asset: Normalize(a.AssetType)))
            .Select(g => new { Zone = g.Key.Zone, AssetType = g.Key.Asset,
                Evaluations = g.OrderBy(a => a.EvaluatedAt).ThenBy(a => a.EvaluationId).ToArray() })
            .OrderBy(p => p.Zone, StringComparer.Ordinal).ThenBy(p => p.AssetType, StringComparer.Ordinal).ToArray();
        if (groups.Length == 0) return [];
        var weights = groups.Select(x => x.Evaluations.Average(a => Weight(a.DamageLevel))).ToArray();
        var totalWeight = weights.Sum();
        var shares = weights.Select(w => availableBudget * w / totalWeight).ToArray();
        var amounts = shares.Select(x => decimal.Floor(x * 100m) / 100m).ToArray();
        var cents = decimal.ToInt32((availableBudget - amounts.Sum()) * 100m);
        // Largest remainders, with stable classification order as tie breaker.
        foreach (var i in Enumerable.Range(0, groups.Length).OrderByDescending(i => shares[i] - amounts[i]).ThenBy(i => i).Take(cents))
            amounts[i] += 0.01m;
        return groups.Select((g, i) => new ReconstructionProposal(disasterId, g.Zone, g.AssetType,
            g.Evaluations.Max(e => e.DamageLevel), g.Evaluations, amounts[i]))
            .OrderByDescending(x => x.AverageSeverity).ThenByDescending(x => x.ProposedBudget).ToArray();
    }
    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}

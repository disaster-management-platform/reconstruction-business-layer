using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace Reconstruction.Domain.Models;

public sealed class ReconstructionProposal
{
    private ReconstructionProposal() { }

    public Guid Id { get; private set; }
    public int DisasterId { get; private set; }
    public string Zone { get; private set; } = "";
    public string AssetType { get; private set; } = "";
    public DamageLevel Damage { get; private set; }
    public int AssessmentCount { get; private set; }
    public decimal ProposedBudget { get; private set; }
    public decimal AverageSeverity => GetEvaluations().Count == 0 ? (int)Damage : GetEvaluations().Average(e => (decimal)e.DamageLevel);
    public ProposalPriority Priority { get; private set; }
    public ProposalStatus Status { get; private set; }
    public long Version { get; private set; } = 1;
    public IReadOnlyList<DamageAssessment> Evaluations { get; private set; } = Array.Empty<DamageAssessment>();
    public string ReviewFingerprint { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public ReconstructionProposal(int disasterId, string zone, string assetType, DamageLevel damage,
        int assessmentCount, decimal proposedBudget, ProposalPriority priority)
    {
        if (disasterId <= 0 || string.IsNullOrWhiteSpace(zone) || string.IsNullOrWhiteSpace(assetType)
            || assessmentCount < 1 || proposedBudget < 0) throw new DomainException("Propuesta inválida.");
        Id = Guid.NewGuid(); DisasterId = disasterId; Zone = zone.Trim(); AssetType = assetType.Trim();
        Damage = damage; AssessmentCount = assessmentCount; ProposedBudget = proposedBudget;
        Priority = priority; Status = ProposalStatus.Proposed; CreatedAt = DateTimeOffset.UtcNow;
    }

    public ReconstructionProposal(int disasterId, string zone, string assetType, DamageLevel damage,
        IReadOnlyList<DamageAssessment> evaluations, decimal proposedBudget = 0m)
        : this(disasterId, zone, assetType, damage, evaluations.Count, proposedBudget, ProposalPriority.Unassigned)
    {
        Evaluations = evaluations.ToList().AsReadOnly();
    }

    public IReadOnlyList<DamageAssessment> GetEvaluations() =>
        Evaluations;

    public bool HasSameClassification(ReconstructionProposal other) => string.Equals(Normalize(Zone), Normalize(other.Zone), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Normalize(AssetType), Normalize(other.AssetType), StringComparison.OrdinalIgnoreCase);

    public bool RefreshEvaluations(IReadOnlyList<DamageAssessment> evaluations)
    {
        if (Status != ProposalStatus.Proposed)
            throw new DomainException("Solo se pueden actualizar propuestas pendientes vigentes.");
        if (evaluations.Count == 0 || evaluations.Any(e => e.DisasterId != DisasterId || e.Status != EvaluationStatus.Validated
            || !string.Equals(Normalize(e.Location), Normalize(Zone), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Normalize(e.AssetType), Normalize(AssetType), StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("Las evaluaciones no corresponden a la clasificación de la propuesta.");
        if (Canonical(Evaluations) == Canonical(evaluations)) return false;
        Evaluations = evaluations.ToList().AsReadOnly();
        AssessmentCount = evaluations.Count;
        Damage = evaluations.Max(e => e.DamageLevel);
        Version++;
        return true;
    }

    private static string Normalize(string? value) => string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public bool MarkObsolete()
    {
        if (Status == ProposalStatus.Obsolete) return false;
        if (Status != ProposalStatus.Proposed) throw new DomainException("Una propuesta con proyecto conserva su información histórica.");
        Status = ProposalStatus.Obsolete;
        Version++;
        return true;
    }

    public bool ReconcileProjectEvaluations(IReadOnlyList<DamageAssessment> current)
    {
        if (Status != ProposalStatus.Selected) throw new DomainException("La revisión corresponde a una propuesta con proyecto.");
        var canonical = Canonical(current);
        var changed = Canonical(GetEvaluations()) != canonical;
        var fingerprint = changed ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            canonical))) : "";
        if (ReviewFingerprint == fingerprint) return false;
        ReviewFingerprint = fingerprint;
        Version++;
        return true;
    }

    private static string Canonical(IReadOnlyList<DamageAssessment> evaluations) => System.Text.Json.JsonSerializer.Serialize(
        evaluations.OrderBy(e => e.EvaluationId).Select(e => e with
        {
            EvaluatedAt = e.EvaluatedAt.ToUniversalTime(),
            Budget = decimal.Parse(e.Budget.ToString("G29", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
            Location = Normalize(e.Location).ToUpperInvariant(),
            AssetType = Normalize(e.AssetType).ToUpperInvariant()
        }).ToArray());

    public bool SetProposedBudget(decimal amount)
    {
        if (Status != ProposalStatus.Proposed || amount < 0 || decimal.Round(amount, 2) != amount)
            throw new DomainException("La estimación de la propuesta no es válida.");
        if (ProposedBudget == amount) return false;
        ProposedBudget = amount;
        Version++;
        return true;
    }

    public void Select()
    {
        if (Status != ProposalStatus.Proposed) throw new DomainException("La propuesta no está vigente para crear un proyecto.");
        Status = ProposalStatus.Selected;
        Version++;
    }
}

namespace Reconstruction.Domain.Models;

public sealed class ReconstructionProject
{
    private ReconstructionProject() { }
    public Guid Id { get; private set; }
    public Guid ProposalId { get; private set; }
    public int DisasterId { get; private set; }
    public ProjectStatus Status { get; private set; }
    public decimal FinalBudget { get; private set; }
    public string FundingSource { get; private set; } = "";
    public string FundingSourceId { get; private set; } = "donations";
    public string Responsible { get; private set; } = "";
    public string Contractor { get; private set; } = "";
    public DateTimeOffset? FormalizationSavedAt { get; private set; }
    public string SignedContractReference { get; private set; } = "";
    public DateTimeOffset? ContractSignedAt { get; private set; }
    public string ContractRecordedBy { get; private set; } = "";
    public DateTimeOffset? BudgetLockedAt { get; private set; }
    public bool IsBudgetLocked => BudgetLockedAt.HasValue || Status != ProjectStatus.Draft;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public long Version { get; private set; } = 1;
    private readonly List<ProjectActivity> _activities = new();
    public IReadOnlyCollection<ProjectActivity> Activities => _activities.AsReadOnly();

    public ReconstructionProject(ReconstructionProposal proposal)
    {
        if (proposal.Status != ProposalStatus.Proposed) throw new DomainException("Solo una propuesta vigente puede originar un proyecto.");
        Id = Guid.NewGuid(); ProposalId = proposal.Id; DisasterId = proposal.DisasterId;
        CreatedAt = DateTimeOffset.UtcNow; Status = ProjectStatus.Draft;
    }

    public void SaveFormalization(decimal budget, string fundingSource, string responsible, string contractor,
        IReadOnlyList<ActivityDefinition> schedule, string fundingSourceId = "donations")
    {
        if (IsBudgetLocked) throw new DomainException("El presupuesto está bloqueado; no se puede modificar la formalización.");
        if (budget <= 0 || decimal.Round(budget, 2) != budget || string.IsNullOrWhiteSpace(fundingSourceId) || fundingSourceId.Length > 200 || string.IsNullOrWhiteSpace(fundingSource) || string.IsNullOrWhiteSpace(responsible)
            || string.IsNullOrWhiteSpace(contractor) || schedule is null || schedule.Count == 0)
            throw new DomainException("El presupuesto, financiación, responsables y cronograma son obligatorios.");
        if (fundingSource.Trim().Length > 200 || responsible.Trim().Length > 200 || contractor.Trim().Length > 200)
            throw new DomainException("La financiación, el responsable y el contratista admiten hasta 200 caracteres.");
        if (schedule.Any(a => a is null)) throw new DomainException("El cronograma contiene una actividad inválida.");
        if (schedule.Sum(a => a.Budget) > budget) throw new DomainException("La suma de las actividades supera el presupuesto final.");
        if (schedule.Any(a => a.Budget < 0 || a.DueAt < a.StartsAt || string.IsNullOrWhiteSpace(a.Name)
            || string.IsNullOrWhiteSpace(a.Responsible)
            || a.StartsAt == default || a.DueAt == default || a.Name.Trim().Length > 250 || a.Responsible.Trim().Length > 200)) throw new DomainException("Existe una actividad inválida.");
        FinalBudget = budget; FundingSourceId = fundingSourceId; FundingSource = fundingSource.Trim(); Responsible = responsible.Trim();
        Contractor = contractor.Trim();
        _activities.Clear();
        _activities.AddRange(schedule.Select(a => new ProjectActivity(Id, a)));
        FormalizationSavedAt = DateTimeOffset.UtcNow; Version++;
    }

    public bool ConfirmFormalization()
    {
        if (Status == ProjectStatus.Monitoring) return false;
        if (Status != ProjectStatus.Draft || FormalizationSavedAt is null || Activities.Count == 0)
            throw new DomainException("Guarde la formalización antes de confirmarla.");
        BudgetLockedAt = DateTimeOffset.UtcNow;
        Status = ProjectStatus.Monitoring;
        Version++;
        return true;
    }

    public void ReviseBudget(decimal amount, IReadOnlyDictionary<Guid, decimal>? activityBudgets = null)
    {
        activityBudgets ??= new Dictionary<Guid, decimal>();
        if (activityBudgets.Any(item => Activities.All(a => a.Id != item.Key) || item.Value < 0 || decimal.Round(item.Value, 2) != item.Value
            || item.Value < Activities.Single(a => a.Id == item.Key).GetActualCost()))
            throw new DomainException("Los presupuestos de actividad deben corresponder al proyecto y cubrir lo ejecutado.");
        if (Status != ProjectStatus.Monitoring || amount <= 0
            || amount < Activities.Sum(a => activityBudgets.GetValueOrDefault(a.Id, a.Budget))
            || amount < Activities.Sum(a => a.GetActualCost()) || decimal.Round(amount, 2) != amount)
            throw new DomainException("El presupuesto revisado debe cubrir las actividades y los costos registrados de un proyecto en monitoreo.");
        foreach (var activity in Activities)
            if (activityBudgets.TryGetValue(activity.Id, out var budget)) activity.ReviseBudget(budget);
        FinalBudget = amount;
        Version++;
    }

    // Registra un documento ya firmado fuera del sistema; no ejecuta una firma electrónica.
    public bool RegisterSignedContract(string reference, DateTimeOffset signedAt, string actor)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Trim().Length > 1000
            || signedAt == default || signedAt > DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(actor))
            throw new DomainException("Se requiere la referencia del contrato firmado, una fecha de firma válida y el usuario que lo registra.");
        if (BudgetLockedAt.HasValue && SignedContractReference == reference.Trim() && ContractSignedAt == signedAt)
            return false;
        if (BudgetLockedAt.HasValue || (Status != ProjectStatus.Draft && Status != ProjectStatus.Formalized))
            throw new DomainException("La formalización ya está bloqueada.");
        if ((FormalizationSavedAt is null && Status != ProjectStatus.Formalized) || FinalBudget <= 0
            || string.IsNullOrWhiteSpace(FundingSource) || string.IsNullOrWhiteSpace(Responsible)
            || string.IsNullOrWhiteSpace(Contractor) || Activities.Count == 0)
            throw new DomainException("Primero guarde el presupuesto, financiación, responsable, contratista y cronograma.");
        SignedContractReference = reference.Trim(); ContractSignedAt = signedAt;
        ContractRecordedBy = actor.Trim(); BudgetLockedAt = DateTimeOffset.UtcNow;
        Status = ProjectStatus.Monitoring; Version++;
        return true;
    }

    public Guid RegisterProgress(Guid activityId, ActivityStatus nextStatus, decimal actualCost,
        IReadOnlyList<string> evidence, IReadOnlyList<string> documents, string actor, Guid? requestId = null)
    {
        if (Status != ProjectStatus.Monitoring) throw new DomainException("El proyecto no está en monitoreo.");
        var activity = Activities.SingleOrDefault(a => a.Id == activityId)
            ?? throw new DomainException("La actividad no pertenece al proyecto.");
        var reportId = activity.RegisterProgress(nextStatus, actualCost, evidence, documents, actor, requestId);
        Version++;
        return reportId;
    }

    public bool CanFinalize() => Status == ProjectStatus.Monitoring && Activities.Count > 0 &&
        Activities.All(a => a.IsCompleteWithEvidence);

    public void Close()
    {
        if (Status != ProjectStatus.Monitoring) throw new DomainException("Solo se pueden cerrar proyectos en monitoreo.");
        if (!CanFinalize())
            throw new DomainException("Todas las actividades deben estar terminadas y tener un reporte final con evidencias y documentación para finalizar el proyecto.");
        Status = ProjectStatus.Closed; ClosedAt = DateTimeOffset.UtcNow; Version++;
    }
}

public sealed record ActivityDefinition(string Name, string Responsible, DateTimeOffset StartsAt,
    DateTimeOffset DueAt, decimal Budget);

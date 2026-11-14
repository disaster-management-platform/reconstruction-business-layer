
namespace Reconstruction.Domain.Models;

public sealed class ProjectActivity
{
    private ProjectActivity() { }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = "";
    public string Responsible { get; private set; } = "";
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public decimal Budget { get; private set; }
    public ActivityStatus Status { get; private set; }
    private readonly List<ProgressReport> _reports = new();
    public IReadOnlyCollection<ProgressReport> Reports => _reports.AsReadOnly();

    public bool IsCompleteWithEvidence => Status == ActivityStatus.Completed &&
        Reports.Any(r => r.Status == ActivityStatus.Completed && r.HasRequiredEvidence);

    public decimal GetActualCost() => Reports.Sum(r => r.ActualCost);

    public ProjectActivity(Guid projectId, ActivityDefinition definition)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; Name = definition.Name.Trim();
        Responsible = definition.Responsible.Trim(); StartsAt = definition.StartsAt; DueAt = definition.DueAt;
        Budget = definition.Budget; Status = ActivityStatus.Pending;
    }

    internal void ReviseBudget(decimal amount) => Budget = amount;

    public Guid RegisterProgress(ActivityStatus nextStatus, decimal cost,
        IReadOnlyList<string> evidence, IReadOnlyList<string> documents, string actor, Guid? requestId = null)
    {
        if (!Enum.IsDefined(nextStatus)) throw new DomainException("El estado de la actividad no es válido.");
        if (IsCompleteWithEvidence) throw new DomainException("La actividad ya fue resuelta con su reporte final.");
        // Una actividad marcada terminada en el cronograma todavía necesita su reporte final.
        if (Status == ActivityStatus.Completed && nextStatus != ActivityStatus.Completed)
            throw new DomainException("Una actividad terminada no puede reabrirse.");
        if (Status == ActivityStatus.InProgress && nextStatus == ActivityStatus.Pending)
            throw new DomainException("Una actividad en ejecución no puede volver a pendiente.");
        if (cost < 0 || decimal.Round(cost, 2) != cost) throw new DomainException("El costo del reporte no puede ser negativo.");
        if (cost > Budget - GetActualCost())
            throw new DomainException("El costo acumulado supera el presupuesto asignado a la actividad.");
        var report = new ProgressReport(Id, nextStatus, cost, evidence, documents, actor, requestId);
        _reports.Add(report); Status = nextStatus;
        return report.Id;
    }
}

public sealed class ProgressReport
{
    private ProgressReport() { }
    public Guid Id { get; private set; }
    public Guid ActivityId { get; private set; }
    public Guid? RequestId { get; private set; }
    public ActivityStatus Status { get; private set; }
    public decimal ActualCost { get; private set; }
    public IReadOnlyList<string> Evidence { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Documents { get; private set; } = Array.Empty<string>();
    public string Actor { get; private set; } = "";
    public DateTimeOffset RegisteredAt { get; private set; }
    public bool HasRequiredEvidence => ValidReferences(GetEvidence()) && ValidReferences(GetDocuments());

    public IReadOnlyList<string> GetEvidence() => Evidence;
    public IReadOnlyList<string> GetDocuments() => Documents;

    private static bool ValidReferences(IReadOnlyList<string>? references) =>
        references is { Count: > 0 } && references.All(x => !string.IsNullOrWhiteSpace(x));

    public ProgressReport(Guid activityId, ActivityStatus status, decimal cost,
        IReadOnlyList<string> evidence, IReadOnlyList<string> documents, string actor, Guid? requestId = null)
    {
        if (activityId == Guid.Empty || !Enum.IsDefined(status) || cost < 0
            || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            throw new DomainException("Los datos del reporte no son válidos.");
        if (!ValidReferences(evidence) || !ValidReferences(documents))
            throw new DomainException("Cada reporte requiere al menos una evidencia y un documento con referencias válidas.");
        Id = Guid.NewGuid(); RequestId = requestId; ActivityId = activityId; Status = status; ActualCost = cost;
        Evidence = evidence.Select(x => x.Trim()).ToList().AsReadOnly();
        Documents = documents.Select(x => x.Trim()).ToList().AsReadOnly();
        Actor = actor.Trim(); RegisteredAt = DateTimeOffset.UtcNow;
    }
}

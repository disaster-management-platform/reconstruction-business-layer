using System.Text.Json.Serialization;
using System.Text.Json;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;

namespace Reconstruction.Application.Messaging;

public interface IIntegrationEventData
{
    string EventType { get; }
}

public static class EventPayloadJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new DamageLevelEventConverter(), new JsonStringEnumConverter() }
    };
    public static JsonElement Serialize(object data) => JsonSerializer.SerializeToElement(data, data.GetType(), Options);
    public static T? Read<T>(JsonElement data) => data.Deserialize<T>(Options);
}

public sealed record ProposalReviewData(Guid ProposalId, string Zone, string AssetType,
    DamageLevel Damage, decimal ProposedBudget, int AssessmentCount);
public sealed record ScheduledActivityData(Guid ActivityId, string Name, string Responsible,
    DateTimeOffset StartsAt, DateTimeOffset DueAt, decimal Budget);

public sealed record ProjectEvaluationReviewData(Guid ProjectId, Guid ProposalId, bool RequiresReview, string Fingerprint,
    IReadOnlyList<DamageAssessment> OriginalEvaluations,
    IReadOnlyList<DamageAssessment> CurrentEvaluations, string Reason);
public sealed record ProposalsGeneratedEvent(IReadOnlyList<ProposalReviewData> Proposals,
    IReadOnlyList<ProjectEvaluationReviewData>? ProjectReviews = null) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.proposals.generated";
}
// La selección sigue siendo individual: una propuesta origina un proyecto.
public sealed record ProposalsSelectedEvent(Guid ProjectId, Guid ProposalId, long ProjectVersion) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.proposals.selected";
}
public sealed record FormalizationSavedEvent(Guid ProjectId, long ProjectVersion, decimal FinalBudget,
    string FundingSource, string Responsible, string Contractor) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.formalization.saved";
}
public sealed record ProjectFormalizedEvent(Guid ProjectId, long ProjectVersion, decimal FinalBudget,
    string ContractReference, DateTimeOffset? SignedAt, IReadOnlyList<ScheduledActivityData> Activities) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.project.formalized";
}
public sealed record ActivityCompletedEvent(Guid ProjectId, Guid ActivityId, Guid ReportId,
    decimal ActualCost, long ProjectVersion) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.activity.completed";
}
public sealed record ActivityProgressEvent(Guid ProjectId, Guid ActivityId, Guid ReportId,
    decimal ActualCost, ActivityStatus Status, long ProjectVersion) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.activity.progress";
}
public sealed record ProjectClosedEvent(Guid ProjectId, long ProjectVersion, DateTimeOffset ClosedAt,
    decimal FinalBudget, decimal TotalExecuted, int TotalActivities, int TotalReports) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.project.closed";
}

public sealed record ProjectBudgetRevisedEvent(Guid ProjectId, long ProjectVersion, decimal BeforeBudget,
    decimal AfterBudget, string Reason) : IIntegrationEventData
{
    [JsonIgnore] public string EventType => "reconstruction.project.budget-revised";
}

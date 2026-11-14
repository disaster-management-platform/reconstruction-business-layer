using System.Text.Json.Serialization;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;

namespace Reconstruction.Application.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProjectRequest(Guid ProposalId, [property: JsonRequired] long ExpectedVersion);
public sealed record FormalizeProjectRequest(
    decimal FinalBudget, string FundingSource, string Responsible,
    string Contractor, IReadOnlyList<ActivityDefinition> Activities, long ExpectedVersion)
{
    [JsonRequired] public string FundingSourceId { get; init; } = "donations";
}
public sealed record ReviseBudgetRequest(decimal Budget, string Reason, long ExpectedVersion)
{
    public IReadOnlyDictionary<Guid, decimal>? ActivityBudgets { get; init; }
}
public sealed record SignedContractRequest(string ContractReference, DateTimeOffset SignedAt, long ExpectedVersion);
public sealed record RegisterProgressRequest(
    [property: JsonRequired] ActivityStatus Status,
    [property: JsonRequired] decimal ActualCost,
    [property: JsonRequired] IReadOnlyList<string> Evidence,
    [property: JsonRequired] IReadOnlyList<string> Documents,
    [property: JsonRequired] long ExpectedVersion)
{
    [JsonRequired] public Guid RequestId { get; init; } = Guid.NewGuid();
}

public sealed record ProposalDto(Guid Id, int DisasterId, string Zone, string AssetType,
    DamageLevel Damage, int AssessmentCount, decimal ProposedBudget,
    ProposalPriority Priority, ProposalStatus Status,
    IReadOnlyList<DamageAssessment> Evaluations, long Version, decimal AverageSeverity);
public sealed record ReportDto(Guid Id, ActivityStatus Status, decimal ActualCost,
    string Actor, DateTimeOffset RegisteredAt,
    IReadOnlyList<string> Evidence, IReadOnlyList<string> Documents, Guid? RequestId);
public sealed record ActivityDto(Guid Id, string Name, string Responsible, DateTimeOffset StartsAt,
    DateTimeOffset DueAt, decimal Budget, ActivityStatus Status, IReadOnlyList<ReportDto> Reports,
    decimal ActualCost, decimal RemainingBudget, bool IsResolved);
public sealed record MonitoringSummaryDto(int TotalActivities, int PendingActivities, int InProgressActivities,
    int CompletedActivities, int ResolvedActivities, int ReportsCount, decimal ProgressPercentage,
    decimal TotalCost, decimal RemainingBudget, DateTimeOffset? LastReportAt, bool CanFinalize);
public sealed record MonitoringDto(ProjectDto Project, MonitoringSummaryDto Summary);
public sealed record FinalReportDto(Guid ProjectId, int DisasterId, ProjectStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, decimal FinalBudget, decimal TotalExecuted,
    decimal RemainingBudget, string FundingSource, string Responsible, string Contractor,
    string SignedContractReference, DateTimeOffset? ContractSignedAt, int TotalActivities,
    int TotalReports, int ActivitiesWithoutReports, IReadOnlyList<ActivityDto> Activities, string FundingSourceId);
public sealed record ProjectDto(Guid Id, Guid ProposalId, int DisasterId, ProjectStatus Status,
    decimal FinalBudget, string FundingSource, string Responsible, string Contractor,
    DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, long Version,
    IReadOnlyList<ActivityDto> Activities, DateTimeOffset? FormalizationSavedAt,
    bool IsBudgetLocked, string SignedContractReference, DateTimeOffset? ContractSignedAt,
    string ContractRecordedBy, DateTimeOffset? BudgetLockedAt, string FundingSourceId);

public static class Projection
{
    public static ProposalDto ToDto(this ReconstructionProposal p) => new(p.Id, p.DisasterId,
        p.Zone, p.AssetType, p.Damage, p.AssessmentCount, p.ProposedBudget, p.Priority, p.Status,
        p.GetEvaluations(), p.Version, p.AverageSeverity);
    public static ProjectDto ToDto(this ReconstructionProject p) => new(p.Id, p.ProposalId,
        p.DisasterId, p.Status, p.FinalBudget, p.FundingSource, p.Responsible, p.Contractor,
        p.CreatedAt, p.ClosedAt, p.Version, p.Activities.OrderBy(a => a.StartsAt).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(a =>
            new ActivityDto(a.Id, a.Name, a.Responsible, a.StartsAt, a.DueAt,
                a.Budget, a.Status, a.Reports.OrderBy(x => x.RegisteredAt).ThenBy(x => x.Id).Select(x => new ReportDto(x.Id, x.Status,
                    x.ActualCost, x.Actor, x.RegisteredAt,
                    x.GetEvidence(), x.GetDocuments(), x.RequestId)).ToArray(), a.GetActualCost(),
                    a.Budget - a.GetActualCost(), a.IsCompleteWithEvidence)).ToArray(),
        p.FormalizationSavedAt, p.IsBudgetLocked, p.SignedContractReference, p.ContractSignedAt, p.ContractRecordedBy, p.BudgetLockedAt, p.FundingSourceId);
}

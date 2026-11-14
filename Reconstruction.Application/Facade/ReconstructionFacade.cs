using Reconstruction.Application.Contracts;
using Reconstruction.Application.Services;
using Reconstruction.Application.Ports;
using Reconstruction.Application.Messaging;

namespace Reconstruction.Application.Facade;

// Orquesta los casos de uso, NO define las reglas del dominio ni abre conexiones.
public sealed class ReconstructionFacade(
    ProposalDefinitionService definitions, ProjectFormalizationService formalization,
    ProjectMonitoringService monitoring, ProjectClosureService closure,
    IReconstructionHistoryReader history, WorkflowService workflow, FundingService funding) : IReconstructionFacade
{
    public Task<IReadOnlyList<WorkflowItemDto>> GetWorkflowAsync(int? disasterId, Guid? projectId, CancellationToken ct) =>
        workflow.ListAsync(disasterId, projectId, ct);
    public Task<IReadOnlyList<ProposalDto>> GenerateProposalsAsync(int id, string actor, CancellationToken ct) =>
        definitions.GenerateAsync(id, actor, ct);
    public Task<IReadOnlyList<ProposalDto>> GetProposalsAsync(int id, CancellationToken ct) =>
        definitions.ListAsync(id, ct);
    public Task<ProjectDto> CreateProjectAsync(Guid id, long expectedVersion, string actor, CancellationToken ct) =>
        formalization.CreateFromProposalAsync(id, expectedVersion, actor, ct);
    public Task<IReadOnlyList<ProjectDto>> GetProjectsAsync(int? disasterId, CancellationToken ct) => monitoring.ListAsync(disasterId, ct);
    public Task<ProjectDto> GetProjectAsync(Guid id, CancellationToken ct) => monitoring.FindAsync(id, ct);
    public Task<MonitoringDto> GetMonitoringAsync(Guid id, CancellationToken ct) => monitoring.GetMonitoringAsync(id, ct);
    public Task<ProjectDto> FormalizeAsync(Guid id, FormalizeProjectRequest req, string actor, CancellationToken ct) =>
        formalization.FormalizeAsync(id, req, actor, ct);
    public Task<FundingDto> GetFundingAsync(int disasterId, CancellationToken ct) => funding.GetAsync(disasterId, ct);
    public Task<ProjectDto> ConfirmFormalizationAsync(Guid id, long version, string actor, CancellationToken ct) =>
        formalization.ConfirmAsync(id, version, actor, ct);
    public Task<ProjectDto> ReviseBudgetAsync(Guid id, ReviseBudgetRequest request, string actor, CancellationToken ct) =>
        formalization.ReviseBudgetAsync(id, request, actor, ct);
    public Task<ProjectDto> RegisterSignedContractAsync(Guid id, SignedContractRequest request, string actor, CancellationToken ct) =>
        formalization.RegisterSignedContractAsync(id, request, actor, ct);
    public Task<ProjectDto> RegisterProgressAsync(Guid id, Guid activityId, RegisterProgressRequest req, string actor, CancellationToken ct) =>
        monitoring.ReportAsync(id, activityId, req, actor, ct);
    public Task<IReadOnlyList<HistoryRecord>> GetAuditAsync(Guid id, CancellationToken ct) => history.GetAuditAsync(id, ct);
    public Task<IReadOnlyList<HistoryRecord>> GetTimelineAsync(Guid id, CancellationToken ct) => history.GetTimelineAsync(id, ct);
    public Task<ProjectDto> CloseAsync(Guid id, long version, string actor, CancellationToken ct) =>
        closure.CloseAsync(id, version, actor, ct);
    public Task<FinalReportDto> GetFinalReportAsync(Guid id, CancellationToken ct) => closure.GetFinalReportAsync(id, ct);
}

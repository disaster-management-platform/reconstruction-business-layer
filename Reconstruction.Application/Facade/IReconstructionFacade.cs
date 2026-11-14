using Reconstruction.Application.Contracts;
using Reconstruction.Application.Ports;
using Reconstruction.Application.Messaging;

namespace Reconstruction.Application.Facade;

// Puerto de entrada: REST, workers y otras interfaces usan la misma fachada.
public interface IReconstructionFacade
{
    Task<IReadOnlyList<WorkflowItemDto>> GetWorkflowAsync(int? disasterId, Guid? projectId, CancellationToken ct);
    Task<IReadOnlyList<ProposalDto>> GenerateProposalsAsync(int disasterId, string actor, CancellationToken ct);
    Task<IReadOnlyList<ProposalDto>> GetProposalsAsync(int disasterId, CancellationToken ct);
    Task<ProjectDto> CreateProjectAsync(Guid proposalId, long expectedVersion, string actor, CancellationToken ct);
    Task<IReadOnlyList<ProjectDto>> GetProjectsAsync(int? disasterId, CancellationToken ct);
    Task<ProjectDto> GetProjectAsync(Guid projectId, CancellationToken ct);
    Task<MonitoringDto> GetMonitoringAsync(Guid projectId, CancellationToken ct);
    Task<ProjectDto> FormalizeAsync(Guid projectId, FormalizeProjectRequest request, string actor, CancellationToken ct);
    Task<ProjectDto> ConfirmFormalizationAsync(Guid projectId, long expectedVersion, string actor, CancellationToken ct);
    Task<ProjectDto> ReviseBudgetAsync(Guid projectId, ReviseBudgetRequest request, string actor, CancellationToken ct);
    Task<Reconstruction.Application.Services.FundingDto> GetFundingAsync(int disasterId, CancellationToken ct);
    Task<ProjectDto> RegisterSignedContractAsync(Guid projectId, SignedContractRequest request, string actor, CancellationToken ct);
    Task<ProjectDto> RegisterProgressAsync(Guid projectId, Guid activityId,
        RegisterProgressRequest request, string actor, CancellationToken ct);
    Task<IReadOnlyList<HistoryRecord>> GetAuditAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<HistoryRecord>> GetTimelineAsync(Guid projectId, CancellationToken ct);
    Task<ProjectDto> CloseAsync(Guid projectId, long expectedVersion, string actor, CancellationToken ct);
    Task<FinalReportDto> GetFinalReportAsync(Guid projectId, CancellationToken ct);
}

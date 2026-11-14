using Reconstruction.Application.Contracts;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Application.Messaging;

namespace Reconstruction.Application.Services;

public sealed class ProjectMonitoringService(IProjectRepository projects, IReconstructionUnitOfWork work, IReportAttachmentVerifier attachments)
{
    public async Task<ProjectDto> ReportAsync(Guid projectId, Guid activityId,
        RegisterProgressRequest request, string actor, CancellationToken ct)
    {
        var project = await GetAsync(projectId, ct);
        if (request.RequestId == Guid.Empty) throw new DomainException("RequestId debe ser un UUID distinto de cero.");
        var repeated = project.Activities.SelectMany(a => a.Reports).SingleOrDefault(r => r.RequestId == request.RequestId);
        if (repeated is not null)
        {
            if (repeated.ActivityId != activityId || repeated.Status != request.Status || repeated.ActualCost != request.ActualCost
                || !repeated.GetEvidence().SequenceEqual((request.Evidence ?? []).Select(x => x?.Trim()))
                || !repeated.GetDocuments().SequenceEqual((request.Documents ?? []).Select(x => x?.Trim())))
                throw new BusinessConflictException("RequestId ya identifica un reporte con otro contenido.");
            return project.ToDto();
        }
        ProjectVersionGuard.EnsureCurrent(project.Version, request.ExpectedVersion);
        await attachments.VerifyAsync(projectId, request.Evidence ?? [], request.Documents ?? [], ct);
        var before = project.ToDto();
        var reportId = project.RegisterProgress(activityId, request.Status, request.ActualCost,
            request.Evidence ?? [], request.Documents ?? [], actor, request.RequestId);
        IIntegrationEventData data = request.Status == ActivityStatus.Completed
            ? new ActivityCompletedEvent(project.Id, activityId, reportId, request.ActualCost, project.Version)
            : new ActivityProgressEvent(project.Id, activityId, reportId, request.ActualCost, request.Status, project.Version);
        await work.CommitAsync(IntegrationEvent.Typed(project.DisasterId, project.Id, actor, data, before, project.ToDto()), ct);
        return project.ToDto();
    }

    public async Task<MonitoringDto> GetMonitoringAsync(Guid projectId, CancellationToken ct)
    {
        var project = await GetAsync(projectId, ct);
        var reports = project.Activities.SelectMany(a => a.Reports).ToArray();
        var resolved = project.Activities.Count(a => a.IsCompleteWithEvidence);
        var totalCost = reports.Sum(r => r.ActualCost);
        return new MonitoringDto(project.ToDto(), new MonitoringSummaryDto(
            project.Activities.Count,
            project.Activities.Count(a => a.Status == ActivityStatus.Pending),
            project.Activities.Count(a => a.Status == ActivityStatus.InProgress),
            project.Activities.Count(a => a.Status == ActivityStatus.Completed),
            resolved, reports.Length,
            project.Activities.Count == 0 ? 0m : decimal.Round(100m * resolved / project.Activities.Count, 2),
            totalCost, project.FinalBudget - totalCost,
            reports.Length == 0 ? null : reports.Max(r => r.RegisteredAt), project.CanFinalize()));
    }

    public async Task<IReadOnlyList<ProjectDto>> ListAsync(int? disasterId, CancellationToken ct) =>
        (await projects.ListAsync(disasterId, ct)).OrderByDescending(p => p.CreatedAt).Select(p => p.ToDto()).ToArray();

    public async Task<ProjectDto> FindAsync(Guid projectId, CancellationToken ct) => (await GetAsync(projectId, ct)).ToDto();

    private async Task<ReconstructionProject> GetAsync(Guid id, CancellationToken ct) =>
        await projects.FindAsync(id, ct) ?? throw new MissingResourceException("Proyecto no encontrado.");
}

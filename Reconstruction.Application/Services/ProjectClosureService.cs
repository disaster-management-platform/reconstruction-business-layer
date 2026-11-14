using Reconstruction.Application.Contracts;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Application.Messaging;

namespace Reconstruction.Application.Services;

public sealed class ProjectClosureService(IProjectRepository projects, IReconstructionUnitOfWork work, IFundingReservationRepository reservations)
{
    public async Task<ProjectDto> CloseAsync(Guid projectId, long expectedVersion,
        string actor, CancellationToken ct)
    {
        var project = await projects.FindAsync(projectId, ct)
            ?? throw new MissingResourceException("Proyecto no encontrado.");
        ProjectVersionGuard.EnsureCurrent(project.Version, expectedVersion);
        if (project.Status == ProjectStatus.Closed) return project.ToDto();
        var before = project.ToDto();
        project.Close();
        var reservation = await reservations.FindAsync(project.DisasterId, ct);
        if (reservation is not null)
            reservation.ReleaseUnspent(project.FinalBudget - project.Activities.Sum(a => a.GetActualCost()));
        var report = BuildFinalReport(project);
        await work.CommitAsync(IntegrationEvent.Typed(project.DisasterId, project.Id, actor,
            new ProjectClosedEvent(project.Id, project.Version, project.ClosedAt!.Value, project.FinalBudget,
                report.TotalExecuted, report.TotalActivities, report.TotalReports), before, project.ToDto()), ct);
        return project.ToDto();
    }

    public async Task<FinalReportDto> GetFinalReportAsync(Guid projectId, CancellationToken ct)
    {
        var project = await projects.FindAsync(projectId, ct)
            ?? throw new MissingResourceException("Proyecto no encontrado.");
        if (project.Status != ProjectStatus.Closed)
            throw new BusinessConflictException("El reporte final está disponible después de finalizar el proyecto.");
        return BuildFinalReport(project);
    }

    private static FinalReportDto BuildFinalReport(ReconstructionProject project)
    {
        var dto = project.ToDto();
        var executed = dto.Activities.Sum(a => a.ActualCost);
        return new FinalReportDto(dto.Id, dto.DisasterId, dto.Status, dto.CreatedAt, dto.ClosedAt,
            dto.FinalBudget, executed, dto.FinalBudget - executed, dto.FundingSource, dto.Responsible,
            dto.Contractor, dto.SignedContractReference, dto.ContractSignedAt, dto.Activities.Count,
            dto.Activities.Sum(a => a.Reports.Count), dto.Activities.Count(a => a.Reports.Count == 0), dto.Activities, dto.FundingSourceId);
    }
}

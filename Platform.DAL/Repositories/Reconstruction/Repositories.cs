using Microsoft.EntityFrameworkCore;
using Reconstruction.Application.Ports;
using Reconstruction.Domain.Models;
using Platform.DAL.Contexts;

namespace Platform.DAL.Repositories.Reconstruction;

public sealed class ProposalRepository(ReconstructionDbContext context, IDisasterAccess access) : IProposalRepository
{
    public async Task<IReadOnlyList<ReconstructionProposal>> ForDisasterAsync(int id, CancellationToken ct) =>
        await context.Proposals.Where(p => p.DisasterId == id && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(p.DisasterId))).OrderBy(p => p.Zone)
            .ThenBy(p => p.AssetType).ToListAsync(ct);
    public Task<ReconstructionProposal?> FindAsync(Guid id, CancellationToken ct) =>
        context.Proposals.SingleOrDefaultAsync(x => x.Id == id && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(x.DisasterId)), ct);
    public void AddRange(IEnumerable<ReconstructionProposal> data) => context.Proposals.AddRange(data);
}

public sealed class ProjectRepository(ReconstructionDbContext context, IDisasterAccess access) : IProjectRepository
{
    public async Task<IReadOnlyList<ReconstructionProject>> ListAsync(int? disasterId, CancellationToken ct) =>
        await context.Projects.AsNoTracking().Where(p => (disasterId == null || p.DisasterId == disasterId) && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(p.DisasterId)))
            .Include(p => p.Activities).ThenInclude(a => a.Reports).ToListAsync(ct);
    public Task<ReconstructionProject?> FindAsync(Guid id, CancellationToken ct) =>
        context.Projects.Include(p => p.Activities).ThenInclude(a => a.Reports)
            .SingleOrDefaultAsync(p => p.Id == id && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(p.DisasterId)), ct);
    public Task<ReconstructionProject?> ByProposalAsync(Guid id, CancellationToken ct) =>
        context.Projects.Include(p => p.Activities).ThenInclude(a => a.Reports)
            .SingleOrDefaultAsync(p => p.ProposalId == id && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(p.DisasterId)), ct);
    public void Add(ReconstructionProject project) => context.Projects.Add(project);
}

public sealed class FundingReservationRepository(ReconstructionDbContext context) : IFundingReservationRepository
{
    public Task<DisasterFundingReservation?> FindAsync(int disasterId, CancellationToken ct) =>
        context.FundingReservations.SingleOrDefaultAsync(x => x.DisasterId == disasterId, ct);
    public void Add(DisasterFundingReservation reservation) => context.FundingReservations.Add(reservation);
}

public sealed class WorkflowRepository(ReconstructionDbContext context, IDisasterAccess access) : IWorkflowRepository
{
    public Task<WorkflowItem?> FindAsync(WorkflowKind kind, Guid subjectId, CancellationToken ct) =>
        context.WorkflowItems.FindAsync(new object[] { kind, subjectId }, ct).AsTask();
    public void Add(WorkflowItem item) => context.WorkflowItems.Add(item);
    public async Task<IReadOnlyList<WorkflowItem>> ListActiveAsync(int? disasterId, Guid? projectId, CancellationToken ct) =>
        await context.WorkflowItems.AsNoTracking().Where(x => x.IsActive
            && (access.AllowedDisasters == null || access.AllowedDisasters.Contains(x.DisasterId))
            && (disasterId == null || x.DisasterId == disasterId) && (projectId == null || x.ProjectId == projectId)).ToListAsync(ct);
}

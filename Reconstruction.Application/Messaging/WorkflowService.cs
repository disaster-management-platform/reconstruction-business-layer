using System.Text.Json;
using Reconstruction.Application.Ports;
using Reconstruction.Domain.Models;

namespace Reconstruction.Application.Messaging;

public sealed record WorkflowItemDto(WorkflowKind Kind, Guid SubjectId, int DisasterId, Guid? ProjectId,
    Guid? ProposalId, DateTimeOffset UpdatedAt, JsonElement Data);
public sealed record ClosureAvailableData(Guid ProjectId, long ProjectVersion, int ResolvedActivities, int TotalActivities);

public sealed class WorkflowService(IWorkflowRepository repository)
{
    public async Task<IReadOnlyList<WorkflowItemDto>> ListAsync(int? disasterId, Guid? projectId, CancellationToken ct) =>
        (await repository.ListActiveAsync(disasterId, projectId, ct)).OrderByDescending(x => x.UpdatedAt)
            .Select(x => new WorkflowItemDto(x.Kind, x.SubjectId, x.DisasterId, x.ProjectId, x.ProposalId,
                x.UpdatedAt, JsonSerializer.Deserialize<JsonElement>(x.DataJson))).ToArray();

    public async Task PutAsync(WorkflowKind kind, Guid subjectId, Guid? projectId, Guid? proposalId,
        EventEnvelope envelope, bool active, object data, CancellationToken ct)
    {
        var item = await repository.FindAsync(kind, subjectId, ct);
        if (item is null)
        {
            item = new WorkflowItem(kind, subjectId, envelope.DisasterId, projectId, proposalId);
            repository.Add(item);
        }
        item.Apply(envelope.Id, envelope.OccurredAt, active, EventPayloadJson.Serialize(data).GetRawText());
    }

    public async Task ResolveAsync(WorkflowKind kind, Guid subjectId, EventEnvelope envelope, CancellationToken ct)
    {
        var item = await repository.FindAsync(kind, subjectId, ct);
        if (item is not null) item.Apply(envelope.Id, envelope.OccurredAt, false, item.DataJson);
    }
}

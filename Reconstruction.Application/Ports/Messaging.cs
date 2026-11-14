using System.Text.Json;
using Reconstruction.Domain;
using Reconstruction.Application.Messaging;
using Reconstruction.Domain.Models;

namespace Reconstruction.Application.Ports;

public sealed record EventEnvelope(Guid Id, string Type, int DisasterId,
    Guid? ProjectId, string Actor, string Detail, DateTimeOffset OccurredAt, JsonElement? Data = null, int SchemaVersion = 1)
{
    // Data ausente identifica un mensaje histórico; nunca se extraen datos de Detail.
    public T? Read<T>() where T : class => Data is null || Data.Value.ValueKind == JsonValueKind.Null
        ? null : EventPayloadJson.Read<T>(Data.Value)
            ?? throw new DomainException("El evento no contiene un payload válido.");
}

public static class ConsumerNames
{
    public const string Formalization = "reconstruction.formalization";
    public const string Monitoring = "reconstruction.monitoring";
    public const string Closure = "reconstruction.closure";
    public const string Timeline = "reconstruction.timeline";
}

public interface IConsumerInbox
{
    Task<bool> HasProcessedAsync(string consumerName, Guid eventId, CancellationToken ct);
    void Record(string consumerName, Guid eventId, string outcomeJson);
}

public interface ITimelineWriter
{
    void Append(EventEnvelope envelope);
}

public interface IWorkflowRepository
{
    Task<WorkflowItem?> FindAsync(WorkflowKind kind, Guid subjectId, CancellationToken ct);
    void Add(WorkflowItem item);
    Task<IReadOnlyList<WorkflowItem>> ListActiveAsync(int? disasterId, Guid? projectId, CancellationToken ct);
}

public interface IWorkflowReplaySource
{
    Task<IReadOnlyList<EventEnvelope>> ReadPublishedAsync(CancellationToken ct);
}

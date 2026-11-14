namespace Platform.DAL.Models;

// Tablas técnicas de Infrastructure: no pertenecen al dominio.
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public long CreatedUnixMs { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
}
public sealed class AuditEntry
{
    public string DataJson { get; set; } = "{}";
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public int DisasterId { get; set; }
    public Guid? ProjectId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
public sealed class InboxMessage
{
    public string ConsumerName { get; set; } = "";
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
    public string OutcomeJson { get; set; } = "";
}
public sealed class TimelineEntry
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public int DisasterId { get; set; }
    public Guid? ProjectId { get; set; }
    public string EventType { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

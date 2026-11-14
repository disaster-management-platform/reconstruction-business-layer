using DisasterLocation.Core;
namespace Platform.DAL.Models;

public sealed class DisasterPhaseAudit
{
    public Guid Id { get; set; }
    public int DisasterId { get; set; }
    public DisasterPhase Before { get; set; }
    public DisasterPhase After { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public long Version { get; set; }
}

namespace DisasterLocation.Core;

public enum DisasterPhase { Registered, Response, Recovery, Closed }
public sealed record ChangeDisasterPhaseRequest(DisasterPhase Phase, long ExpectedVersion);
public sealed record DisasterPhaseChange(int DisasterId, DisasterPhase Before, DisasterPhase After,
    long Version, string Actor, DateTimeOffset OccurredAt);

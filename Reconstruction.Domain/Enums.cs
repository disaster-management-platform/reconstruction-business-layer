namespace Reconstruction.Domain;

public enum DamageLevel { None = 0, Minor = 1, Moderate = 2, Severe = 3, Destroyed = 4 }
public enum ProposalPriority { Unassigned = 0, Low = 1, High = 2 }
public enum ProposalStatus { Proposed = 0, Selected = 1, Obsolete = 2 }
// Formalized conserva el valor histórico 1; el flujo actual es Draft -> Monitoring -> Closed.
public enum ProjectStatus { Draft = 0, Formalized = 1, Monitoring = 2, Closed = 3 }
public enum ActivityStatus { Pending = 0, InProgress = 1, Completed = 2 }

// Contrato inicial de integración: solo las evaluaciones validadas son elegibles.
public enum EvaluationStatus { Draft = 0, Validated = 1, Rejected = 2 }

public enum DisasterPhase { Registered, Response, Recovery, Closed }

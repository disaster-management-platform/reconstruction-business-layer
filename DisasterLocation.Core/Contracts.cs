namespace DisasterLocation.Core;

public sealed record CreateDisasterRequest(
    string? Nombre, string? Ubicacion, int? Severidad,
    string? Descripcion, double? Latitud, double? Longitud, double? Radio = null);
public sealed record DisasterResponse(
    int Id, string Nombre, string Ubicacion, int Severidad,
    string Descripcion, double Latitud, double Longitud, double? Radio = null,
    string Phase = "Registered", long Version = 1);
public sealed record CreateDisasterResult(DisasterResponse Disaster, bool Created);

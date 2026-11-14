namespace DisasterLocation.BLL;

public sealed class DisasterConflictException()
    : Exception("La clave de idempotencia ya se utilizó con un reporte diferente.");

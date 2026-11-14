using DisasterLocation.Core;

namespace DisasterLocation.BLL;

public sealed class DisasterService(IDisasterRepository repository)
{
    public async Task<IReadOnlyList<DisasterResponse>> GetAllAsync(CancellationToken ct = default) =>
        (await repository.GetAllAsync(ct)).Select(ToResponse).ToArray();

    public async Task<DisasterResponse?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var disaster = await repository.GetByIdAsync(id, ct);
        return disaster is null ? null : ToResponse(disaster);
    }

    public async Task<CreateDisasterResult> CreateAsync(CreateDisasterRequest request,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        var clientRequestId = Guid.NewGuid();
        if (idempotencyKey is not null &&
            (!Guid.TryParseExact(idempotencyKey, "D", out clientRequestId) || clientRequestId == Guid.Empty))
            errors["Idempotency-Key"] = ["La clave debe ser un UUID válido distinto de cero."];
        ValidateText("nombre", request.Nombre, 120, errors);
        ValidateText("ubicacion", request.Ubicacion, 200, errors);
        ValidateText("descripcion", request.Descripcion, 2000, errors);
        if (request.Severidad is null or < 1 or > 5) errors["severidad"] = ["Severidad entre 1 y 5."];
        if (request.Latitud is null || !double.IsFinite(request.Latitud.Value) || request.Latitud is < -90 or > 90)
            errors["latitud"] = ["Latitud inválida."];
        if (request.Longitud is null || !double.IsFinite(request.Longitud.Value) || request.Longitud is < -180 or > 180)
            errors["longitud"] = ["Longitud inválida."];
        if (request.Radio is not null && (!double.IsFinite(request.Radio.Value) || request.Radio <= 0 || request.Radio > 1_000_000))
            errors["radio"] = ["El radio debe estar entre 0 y 1.000.000 metros."];
        if (errors.Count > 0) throw new DisasterValidationException(errors);
        var disaster = new Disaster
        {
            ClientRequestId = clientRequestId,
            Nombre = request.Nombre!.Trim(), Ubicacion = request.Ubicacion!.Trim(),
            Descripcion = request.Descripcion!.Trim(), Severidad = request.Severidad!.Value,
            Latitud = request.Latitud!.Value, Longitud = request.Longitud!.Value, Radio = request.Radio
        };
        var existing = await repository.GetByClientRequestIdAsync(clientRequestId, ct);
        if (existing is not null) return Resolve(disaster, new DisasterSaveResult(existing, false));
        return Resolve(disaster, await repository.AddAsync(disaster, ct));
    }

    public async Task<DisasterResponse?> ChangePhaseAsync(int id, ChangeDisasterPhaseRequest request,
        string actor, CancellationToken ct)
    {
        var disaster = await repository.GetByIdAsync(id, ct);
        if (disaster is null) return null;
        if (request.ExpectedVersion != disaster.Version) throw new DisasterConflictException();
        if (!Enum.IsDefined(request.Phase) || (int)request.Phase != (int)disaster.Phase + 1)
            throw new DisasterValidationException(new Dictionary<string, string[]>
            { ["phase"] = ["Las fases siguen el orden Registered → Response → Recovery → Closed."] });
        if (!await repository.ChangePhaseAsync(id, disaster.Phase, request.Phase, request.ExpectedVersion, actor, ct))
            throw new DisasterConflictException();
        return await GetByIdAsync(id, ct);
    }

    private static CreateDisasterResult Resolve(Disaster requested, DisasterSaveResult result)
    {
        var saved = result.Disaster;
        if (requested.Nombre != saved.Nombre || requested.Ubicacion != saved.Ubicacion ||
            requested.Severidad != saved.Severidad || requested.Descripcion != saved.Descripcion ||
            requested.Latitud != saved.Latitud || requested.Longitud != saved.Longitud || requested.Radio != saved.Radio)
            throw new DisasterConflictException();
        return new CreateDisasterResult(ToResponse(saved), result.Created);
    }
    private static void ValidateText(string field, string? text, int max,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) errors[field] = ["Campo obligatorio."];
        else if (text.Trim().Length > max) errors[field] = [$"Máximo {max} caracteres."];
    }
    private static DisasterResponse ToResponse(Disaster d) =>
        new(d.Id, d.Nombre, d.Ubicacion, d.Severidad, d.Descripcion, d.Latitud, d.Longitud, d.Radio, d.Phase.ToString(), d.Version);
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reconstruction.Application;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;

namespace Reconstruction.Infrastructure.ExternalAdapters;

public sealed class DamageAssessmentHttpReader(HttpClient client) : IDamageAssessmentReader
{
    public async Task<IReadOnlyList<DamageAssessment>> GetForDisasterAsync(int disasterId, CancellationToken ct)
    {
        var result = await ExternalModuleHttp.GetAsync<AssessmentResponse>(client,
            $"api/evaluaciones?disasterId={disasterId}", ct);
        if (result.SchemaVersion != 1 || result.DisasterId != disasterId || result.Evaluations is null
            || result.Evaluations.Any(x => x is null || x.DisasterId != disasterId || string.IsNullOrWhiteSpace(x.AssetId) || x.Revision <= 0))
            throw new DependencyUnavailableException("El contrato de evaluación de daños no es válido.");
        return result.Evaluations;
    }
    private sealed record AssessmentResponse(int SchemaVersion, int DisasterId, IReadOnlyList<DamageAssessment> Evaluations);
}

public sealed class FundingHttpReader(HttpClient client) : IFundingReader
{
    public async Task<FundingAvailability> GetAvailableAsync(int disasterId, CancellationToken ct)
    {
        var result = await ExternalModuleHttp.GetAsync<FundingResponse>(client,
            $"api/donaciones/asignaciones/reconstruccion/{disasterId}", ct);
        if (result.SchemaVersion != 1 || result.DisasterId != disasterId || result.AvailableAmount < 0
            || result.Currency != "COP" || string.IsNullOrWhiteSpace(result.Source) || string.IsNullOrWhiteSpace(result.SourceId)
            || result.AsOf > DateTimeOffset.UtcNow.AddMinutes(1) || result.AsOf < DateTimeOffset.UtcNow.AddMinutes(-5))
            throw new DependencyUnavailableException("La asignación de donaciones es inválida o está desactualizada.");
        return new FundingAvailability(disasterId, result.AvailableAmount, result.Source, result.SourceId, result.Currency);
    }
    private sealed record FundingResponse(int SchemaVersion, int DisasterId, decimal AvailableAmount,
        string Source, string SourceId, string Currency, DateTimeOffset AsOf);
}

internal static class ExternalModuleHttp
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, Converters = { new JsonStringEnumConverter() } };
    public static async Task<T> GetAsync<T>(HttpClient client, string path, CancellationToken ct)
    {
        if (client.BaseAddress is null) throw new DependencyUnavailableException("Configure la dirección del módulo externo.");
        try
        {
            using var response = await client.GetAsync(path, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new JsonException("Respuesta vacía.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new DependencyUnavailableException("El módulo externo no está disponible o devolvió un contrato inválido.", ex); }
    }
}

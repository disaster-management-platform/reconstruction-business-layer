using DisasterLocation.Core;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Reconstruction.Domain;
using Reconstruction.Application;
using Reconstruction.Application.Ports;
using Polly;
using Polly.CircuitBreaker;

namespace Reconstruction.Infrastructure.ExternalAdapters;

public sealed class DisasterHttpReader(HttpClient client, ResiliencePipeline<DisasterSnapshot?> circuitBreaker) : IDisasterReader
{
    public async Task<DisasterSnapshot?> GetAsync(int disasterId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await circuitBreaker.ExecuteAsync(async token =>
            {
                using var response = await client.GetAsync($"api/desastres/{disasterId}", token);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                var disaster = await response.Content.ReadFromJsonAsync<DisasterResponse>(token);
                if (disaster is null || disaster.Id != disasterId || disaster.Severidad < 1 || disaster.Severidad > 5 || !Enum.TryParse<DisasterPhase>(disaster.Phase, out _))
                    throw new JsonException("Respuesta de desastre inválida.");
                return new DisasterSnapshot(disaster.Id, disaster.Severidad, Enum.Parse<DisasterPhase>(disaster.Phase));
            }, ct);
        }
        catch (BrokenCircuitException ex)
        {
            throw new DependencyUnavailableException("Servicio de desastres temporalmente no disponible. Reintente más tarde.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DependencyUnavailableException("Servicio de desastres no disponible.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("Tiempo de espera del servicio de desastres agotado.", ex);
        }
        catch (JsonException ex)
        {
            throw new DependencyUnavailableException("El servicio de desastres devolvió datos inválidos.", ex);
        }
    }

    private sealed record DisasterResponse
    {
        public required int Id { get; init; }
        public required int Severidad { get; init; }
        public required string Phase { get; init; }
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Reconstruction.Application;
using Reconstruction.Application.Ports;

namespace Reconstruction.Api;

public sealed class DisasterScopeAccess(IHttpContextAccessor context, IHttpClientFactory clients, Reconstruction.Infrastructure.Messaging.MessageDisasterScope? message = null) : IDisasterAccess
{
    private HashSet<int>? resolved;
    public IReadOnlyCollection<int>? AllowedDisasters => context.HttpContext is { } http
        ? resolved ?? http.User.FindAll("disaster_id")
            .Select(claim => int.TryParse(claim.Value, out var id) ? id : 0).Where(id => id > 0).ToHashSet()
        : message?.AllowedDisasters ?? [];

    public async Task ResolveAsync(CancellationToken ct)
    {
        var http = context.HttpContext!;
        if (!http.User.IsInRole("disasters.admin")) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/desastres");
        request.Headers.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        try
        {
            using var response = await clients.CreateClient("disaster-access").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var disasters = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            resolved = [];
            foreach (var disaster in disasters.EnumerateArray())
            {
                var id = disaster.GetProperty("id").GetInt32();
                if (id <= 0) throw new JsonException("Identificador de desastre inválido.");
                resolved.Add(id);
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("No se pudo verificar el ámbito de desastres. Reintenta más tarde.", error);
        }
    }
}

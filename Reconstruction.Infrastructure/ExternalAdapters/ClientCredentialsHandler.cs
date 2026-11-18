using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Reconstruction.Infrastructure.ExternalAdapters;

internal sealed class ClientCredentialsHandler(IHttpClientFactory clients, IConfiguration configuration, string module)
    : DelegatingHandler
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;
    private int invalidated;
    private DateTimeOffset expires;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var settings = configuration.GetSection($"ExternalApis:{module}:OAuth");
        var endpoint = settings["TokenEndpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidOperationException("OAuth token endpoint must use HTTPS.");
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (Interlocked.Exchange(ref invalidated, 0) != 0 || token is null || expires <= DateTimeOffset.UtcNow)
                {
                    using var body = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = settings["ClientId"] ?? throw new InvalidOperationException("Missing OAuth client ID."),
                        ["client_secret"] = settings["ClientSecret"] ?? throw new InvalidOperationException("Missing OAuth client secret.")
                    });
                    using var response = await clients.CreateClient("oauth").PostAsync(uri, body, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    var lifetime = result.GetProperty("expires_in").GetInt32();
                    var value = result.GetProperty("access_token").GetString();
                    if (lifetime <= 0 || string.IsNullOrWhiteSpace(value) || !string.Equals(result.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                        throw new HttpRequestException("Invalid OAuth token response.");
                    token = value;
                    expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, lifetime - Math.Min(30, lifetime / 5)));
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            finally { gate.Release(); }
        }
        var forwarded = await base.SendAsync(request, cancellationToken);
        // Invalidate for the next operation; never replay a business mutation automatically.
        if (forwarded.StatusCode == System.Net.HttpStatusCode.Unauthorized) Interlocked.Exchange(ref invalidated, 1);
        return forwarded;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) gate.Dispose();
        base.Dispose(disposing);
    }
}

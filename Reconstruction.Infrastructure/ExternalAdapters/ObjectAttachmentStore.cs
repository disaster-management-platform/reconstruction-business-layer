using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Reconstruction.Application;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;

namespace Reconstruction.Infrastructure.ExternalAdapters;

/// <summary>Private immutable objects shared by all reconstruction replicas.</summary>
public sealed class ObjectAttachmentStore(IHttpClientFactory clients, IConfiguration configuration) : AttachmentStore(false)
{
    private const int MetadataLimit = 8192;
    private const int ObjectLimit = (int)MaximumLength + MetadataLimit + 4;
    private readonly SemaphoreSlim operations = new(4, 4);
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string? token;
    private DateTimeOffset expires;

    private string Setting(string key) => configuration[$"Attachments:Storage:{key}"] is { Length: > 0 } value
        ? value : throw Unavailable();
    private Uri Endpoint(string path)
    {
        if (!Uri.TryCreate(Setting("BaseUrl"), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw Unavailable();
        return new Uri(uri, path);
    }
    private string ObjectPath(Guid project, Guid id)
    {
        var bucket = Setting("Bucket");
        if (bucket != "reconstruction-attachments") throw Unavailable();
        return $"{bucket}/{project:N}/{id:N}";
    }
    private static DependencyUnavailableException Unavailable() => new("El almacenamiento privado de adjuntos no está disponible.");

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Endpoint(path));
        request.Headers.Add("apikey", Setting("PublishableKey"));
        return request;
    }
    private async Task<string> AccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        await tokenGate.WaitAsync(ct);
        try
        {
            if (token is not null && expires > DateTimeOffset.UtcNow) return token;
            using var request = Request(HttpMethod.Post, "/auth/v1/token?grant_type=password");
            request.Content = new StringContent(JsonSerializer.Serialize(new { email = Setting("Email"), password = Setting("Password") }), System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 65536, ct));
            var value = json.RootElement.GetProperty("access_token").GetString();
            var lifetime = json.RootElement.GetProperty("expires_in").GetInt32();
            if (string.IsNullOrWhiteSpace(value) || lifetime < 1 || lifetime > 86400) throw Unavailable();
            token = value;
            expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, lifetime - Math.Min(60, lifetime / 5)));
            return value;
        }
        finally { tokenGate.Release(); }
    }
    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        var credential = await AccessTokenAsync(client, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await tokenGate.WaitAsync(ct);
            try { if (token == credential) token = null; }
            finally { tokenGate.Release(); }
            throw Unavailable(); // Do not replay uncertain mutations.
        }
        return response;
    }
    private async Task<T> ExecuteAsync<T>(Func<HttpClient, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (!await operations.WaitAsync(0, ct)) throw Unavailable();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = clients.CreateClient("attachment-storage");
            return await action(client, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException) { throw Unavailable(); }
        finally { operations.Release(); }
    }
    public override Task<AttachmentInfo> SaveAsync(Guid projectId, string name, string contentType, string category, Stream content, CancellationToken ct) =>
        ExecuteAsync(async (client, deadline) =>
        {
            var (info, bytes) = await ReadUploadAsync(projectId, name, category, content, deadline);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(info);
            if (metadata.Length > MetadataLimit) throw new DomainException("Metadatos de adjunto inválidos.");
            var payload = new byte[4 + metadata.Length + bytes.Length];
            BinaryPrimitives.WriteInt32BigEndian(payload, metadata.Length);
            metadata.CopyTo(payload, 4);
            bytes.CopyTo(payload, 4 + metadata.Length);
            using var request = Request(HttpMethod.Post, "/storage/v1/object/" + ObjectPath(projectId, info.Id));
            request.Headers.Add("x-upsert", "false");
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await SendAsync(client, request, deadline);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            return info;
        }, ct);

    public override Task<AttachmentContent?> OpenAsync(Guid projectId, Guid id, CancellationToken ct) =>
        ExecuteAsync<AttachmentContent?>(async (client, deadline) =>
        {
            if (projectId == Guid.Empty || id == Guid.Empty) return null;
            using var request = Request(HttpMethod.Get, "/storage/v1/object/authenticated/" + ObjectPath(projectId, id));
            using var response = await SendAsync(client, request, deadline);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                // Some Storage versions use HTTP 400 for a missing object; distinguish it from policy/config errors.
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    using var error = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 65536, deadline));
                    if (error.RootElement.TryGetProperty("statusCode", out var code) && code.ToString() == "404") return null;
                }
                throw Unavailable();
            }
            var payload = await ReadBoundedAsync(response.Content, ObjectLimit, deadline);
            if (payload.Length < 5) throw new DomainException("Adjunto almacenado inválido.");
            var length = BinaryPrimitives.ReadInt32BigEndian(payload);
            if (length < 1 || length > MetadataLimit || length > payload.Length - 4) throw new DomainException("Metadatos de adjunto inválidos.");
            var info = JsonSerializer.Deserialize<AttachmentInfo>(payload.AsSpan(4, length)) ?? throw new DomainException("Adjunto almacenado inválido.");
            var bytes = payload[(4 + length)..];
            ValidateContent(info, projectId, id, bytes);
            return new AttachmentContent(info, new MemoryStream(bytes, writable: false));
        }, ct);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw Unavailable();
        await using var source = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > limit) throw Unavailable();
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
}

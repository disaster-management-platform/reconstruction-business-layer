using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;

namespace Reconstruction.Infrastructure.ExternalAdapters;

public sealed class FileAttachmentStore(IConfiguration configuration) : IAttachmentStore
{
    public const long MaximumLength = 20 * 1024 * 1024;
    private string Root => Path.GetFullPath(configuration["Attachments:Directory"] ?? "App_Data/attachments");
    private string Metadata(Guid id) => Path.Combine(Root, id.ToString("N") + ".json");
    private string Data(Guid id) => Path.Combine(Root, id.ToString("N") + ".bin");

    public async Task<AttachmentInfo> SaveAsync(Guid projectId, string name, string contentType, string category, Stream content, CancellationToken ct)
    {
        if (projectId == Guid.Empty || category is not ("evidence" or "document"))
            throw new DomainException("Proyecto o categoría de adjunto inválidos.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (Path.GetFileName(name) != name || name.Length > 200 || extension is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
            throw new DomainException("Adjunte un PDF, PNG o JPEG con un nombre de hasta 200 caracteres.");
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int length;
        while ((length = await content.ReadAsync(buffer, ct)) != 0)
        {
            if (memory.Length + length > MaximumLength) throw new DomainException("El adjunto supera los 20 MB.");
            await memory.WriteAsync(buffer.AsMemory(0, length), ct);
        }
        var bytes = memory.ToArray();
        var valid = extension switch
        {
            ".pdf" => bytes.AsSpan().StartsWith("%PDF-"u8),
            ".png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            _ => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
        };
        if (!valid) throw new DomainException("El contenido no corresponde al tipo de archivo indicado.");
        var mime = extension == ".pdf" ? "application/pdf" : extension == ".png" ? "image/png" : "image/jpeg";
        var id = Guid.NewGuid();
        var info = new AttachmentInfo(id, projectId, name, mime, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), category);
        Directory.CreateDirectory(Root);
        try
        {
            await File.WriteAllBytesAsync(Data(id), bytes, ct);
            await File.WriteAllTextAsync(Metadata(id), JsonSerializer.Serialize(info), ct);
        }
        catch
        {
            File.Delete(Data(id)); File.Delete(Metadata(id)); throw;
        }
        return info;
    }
    public async Task<AttachmentContent?> OpenAsync(Guid projectId, Guid id, CancellationToken ct)
    {
        if (!File.Exists(Metadata(id)) || !File.Exists(Data(id))) return null;
        var info = JsonSerializer.Deserialize<AttachmentInfo>(await File.ReadAllTextAsync(Metadata(id), ct));
        if (info is null || info.ProjectId != projectId) return null;
        return new AttachmentContent(info, File.OpenRead(Data(id)));
    }
    public async Task VerifyAsync(Guid projectId, IReadOnlyList<string> evidence, IReadOnlyList<string> documents, CancellationToken ct)
    {
        if (evidence.Count == 0 || documents.Count == 0) throw new DomainException("Cada reporte requiere evidencias y documentación.");
        foreach (var (references, category) in new[] { (evidence, "evidence"), (documents, "document") })
        foreach (var reference in references)
        {
            if (reference is null || !reference.StartsWith("attachment:", StringComparison.Ordinal)
                || !Guid.TryParse(reference[11..], out var id))
            {
                if (configuration.GetValue<bool>("Reconstruction:DemoExternalData") && !string.IsNullOrWhiteSpace(reference)) continue;
                throw new DomainException("Adjunte los soportes al proyecto antes de registrar el reporte.");
            }
            var file = await OpenAsync(projectId, id, ct);
            if (file is null) throw new DomainException("Un adjunto no existe o no pertenece a este proyecto.");
            await using var stream = file.Content;
            if (file.Info.Category != category || file.Info.Length != stream.Length
                || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != file.Info.Sha256)
                throw new DomainException("Un adjunto no corresponde a su categoría o su contenido fue alterado.");
        }
    }
}

using System.Security.Cryptography;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;

namespace Reconstruction.Infrastructure.ExternalAdapters;

public abstract class AttachmentStore(bool allowDemo) : IAttachmentStore
{
    public const long MaximumLength = 20 * 1024 * 1024;
    public abstract Task<AttachmentInfo> SaveAsync(Guid projectId, string name, string contentType, string category, Stream content, CancellationToken ct);
    public abstract Task<AttachmentContent?> OpenAsync(Guid projectId, Guid id, CancellationToken ct);

    protected static async Task<(AttachmentInfo Info, byte[] Data)> ReadUploadAsync(Guid projectId, string name, string category, Stream content, CancellationToken ct)
    {
        if (projectId == Guid.Empty || category is not ("evidence" or "document"))
            throw new DomainException("Proyecto o categoría de adjunto inválidos.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsControl(c) || c == '/' || c == '\\') || Path.GetFileName(name) != name || name.Length > 200 || extension is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
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
        var mime = Mime(extension, bytes);
        if (mime is null) throw new DomainException("El contenido no corresponde al tipo de archivo indicado.");
        var id = Guid.NewGuid();
        var info = new AttachmentInfo(id, projectId, name, mime, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), category);
        return (info, bytes);
    }

    private static string? Mime(string extension, byte[] bytes) => extension switch
    {
        ".pdf" when bytes.AsSpan().StartsWith("%PDF-"u8) => "application/pdf",
        ".png" when bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => "image/png",
        ".jpg" or ".jpeg" when bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 => "image/jpeg",
        _ => null
    };

    protected static void ValidateContent(AttachmentInfo info, Guid projectId, Guid id, byte[] bytes)
    {
        if (info.ProjectId != projectId || info.Id != id || info.Category is not ("evidence" or "document") ||
            string.IsNullOrWhiteSpace(info.Name) || info.Name.Length > 200 || info.Name.Any(c => char.IsControl(c) || c == '/' || c == '\\') ||
            Mime(Path.GetExtension(info.Name).ToLowerInvariant(), bytes) is not { } mime || mime != info.ContentType ||
            info.Length != bytes.LongLength || info.Length <= 0 || info.Length > MaximumLength ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("El adjunto no corresponde al proyecto o su contenido fue alterado.");
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
                if (allowDemo && !string.IsNullOrWhiteSpace(reference)) continue;
                throw new DomainException("Adjunte los soportes al proyecto antes de registrar el reporte.");
            }
            var file = await OpenAsync(projectId, id, ct);
            if (file is null) throw new DomainException("Un adjunto no existe o no pertenece a este proyecto.");
            await using var stream = file.Content;
            if (file.Info.Category != category)
                throw new DomainException("Un adjunto no corresponde a su categoría o su contenido fue alterado.");
        }
    }
}

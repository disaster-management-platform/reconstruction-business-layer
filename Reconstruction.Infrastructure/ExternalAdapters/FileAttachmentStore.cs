using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;

namespace Reconstruction.Infrastructure.ExternalAdapters;

public sealed class FileAttachmentStore(IConfiguration configuration, IHostEnvironment environment) : AttachmentStore(environment.IsDevelopment() && configuration.GetValue<bool>("Reconstruction:DemoExternalData"))
{
    private string Root => Path.GetFullPath(configuration["Attachments:Directory"] ?? "App_Data/attachments");
    private string Metadata(Guid id) => Path.Combine(Root, id.ToString("N") + ".json");
    private string Data(Guid id) => Path.Combine(Root, id.ToString("N") + ".bin");

    public override async Task<AttachmentInfo> SaveAsync(Guid projectId, string name, string contentType, string category, Stream content, CancellationToken ct)
    {
        var (info, bytes) = await ReadUploadAsync(projectId, name, category, content, ct);
        var id = info.Id;
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
    public override async Task<AttachmentContent?> OpenAsync(Guid projectId, Guid id, CancellationToken ct)
    {
        if (!File.Exists(Metadata(id)) || !File.Exists(Data(id))) return null;
        var info = JsonSerializer.Deserialize<AttachmentInfo>(await File.ReadAllTextAsync(Metadata(id), ct));
        if (info is null || info.ProjectId != projectId) return null;
        if (new FileInfo(Data(id)).Length > MaximumLength) throw new DomainException("El adjunto supera los 20 MB.");
        var bytes = await File.ReadAllBytesAsync(Data(id), ct);
        ValidateContent(info, projectId, id, bytes);
        return new AttachmentContent(info, new MemoryStream(bytes, writable: false));
    }
}

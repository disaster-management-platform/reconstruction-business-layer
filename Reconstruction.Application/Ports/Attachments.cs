namespace Reconstruction.Application.Ports;

public sealed record AttachmentInfo(Guid Id, Guid ProjectId, string Name, string ContentType,
    long Length, string Sha256, string Category);
public sealed record AttachmentContent(AttachmentInfo Info, Stream Content);
public interface IReportAttachmentVerifier
{
    Task VerifyAsync(Guid projectId, IReadOnlyList<string> evidence, IReadOnlyList<string> documents, CancellationToken ct);
}
public interface IAttachmentStore : IReportAttachmentVerifier
{
    Task<AttachmentInfo> SaveAsync(Guid projectId, string name, string contentType, string category, Stream content, CancellationToken ct);
    Task<AttachmentContent?> OpenAsync(Guid projectId, Guid id, CancellationToken ct);
}
